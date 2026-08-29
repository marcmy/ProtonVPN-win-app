/*
 * Copyright (c) 2026 Proton AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.LocalAgent;
using ProtonVPN.Common.Legacy.Threading;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Crypto.Contracts;
using ProtonVPN.EntityMapping.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.LocalAgent;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.ProcessCommunication.EntityMapping.Vpn;
using ProtonVPN.Service.ControllerRetries;
using ProtonVPN.Service.ProcessCommunication;
using ProtonVPN.Service.ServerHealth;
using ProtonVPN.Service.Settings;
using ProtonVPN.Service.StateMachine;
using ProtonVPN.Service.Vpn;
using ProtonVPN.Vpn.Connection;
using ProtonVPN.Vpn.LocalAgent;
using ProtonVPN.Vpn.PortMapping;
using CryptoPublicKey = ProtonVPN.Crypto.Contracts.PublicKey;

namespace ProtonVPN.Service.Tests.Vpn;

[TestClass]
public class ConnectionCertificateHardeningTest
{
    [TestMethod]
    public void ConnectionCertificateMapper_ShouldCanonicalizeValidPem()
    {
        ILogger logger = Substitute.For<ILogger>();
        ConnectionCertificateMapper subject = new(logger);
        DateTime expirationDateUtc = DateTime.UtcNow.AddHours(1);

        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=ProtonVPN certificate mapper regression",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));

        string canonicalPem = certificate.ExportCertificatePem();
        string nonCanonicalPem = $"\r\n{canonicalPem.Replace("\n", "\r\n")}\r\n";

        ConnectionCertificate result = subject.Map(new ConnectionCertificateIpcEntity
        {
            Pem = nonCanonicalPem,
            ExpirationDateUtc = expirationDateUtc,
        });

        Assert.AreEqual(canonicalPem, result.Pem);
        Assert.AreEqual(expirationDateUtc, result.ExpirationDateUtc);
    }

    [TestMethod]
    public void ConnectionCertificateMapper_ShouldRejectMalformedPem()
    {
        ConnectionCertificateMapper subject = new(Substitute.For<ILogger>());
        DateTime expirationDateUtc = DateTime.UtcNow.AddHours(1);

        ConnectionCertificate result = subject.Map(new ConnectionCertificateIpcEntity
        {
            Pem = "not a certificate",
            ExpirationDateUtc = expirationDateUtc,
        });

        Assert.AreEqual(string.Empty, result.Pem);
        Assert.AreEqual(expirationDateUtc, result.ExpirationDateUtc);
    }

    [TestMethod]
    public void ConnectionCertificateMapper_ShouldPreserveOutboundCertificate()
    {
        ConnectionCertificateMapper subject = new(Substitute.For<ILogger>());
        DateTime expirationDateUtc = DateTime.UtcNow.AddHours(1);
        ConnectionCertificate certificate = new("pem", expirationDateUtc);

        ConnectionCertificateIpcEntity result = subject.Map(certificate);

        Assert.AreEqual(certificate.Pem, result.Pem);
        Assert.AreEqual(certificate.ExpirationDateUtc, result.ExpirationDateUtc);
    }

    [TestMethod]
    public async Task LocalAgentTlsCredentialsCache_ShouldClearStoredCredentials()
    {
        LocalAgentTlsCredentialsCache subject = new(Substitute.For<ILogger>());
        AsymmetricKeyPair keyPair = CreateKeyPair();
        LocalAgentTlsCredentials credentials = new(
            new ConnectionCertificate("certificate", DateTime.UtcNow.AddHours(1)),
            keyPair);

        await subject.SetAsync(credentials, CancellationToken.None);
        Assert.AreSame(credentials, await subject.GetAsync(CancellationToken.None));

        await subject.ClearAsync(CancellationToken.None);

        Assert.IsNull(await subject.GetAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Connect_ShouldClearCachedLocalAgentTlsCredentials_WhenUsingUsernameAndPassword()
    {
        IEntityMapper entityMapper = Substitute.For<IEntityMapper>();
        ILocalAgentTlsCredentialsCache cache = Substitute.For<ILocalAgentTlsCredentialsCache>();
        IVpnConnectionStateMachine stateMachine = Substitute.For<IVpnConnectionStateMachine>();
        ConnectionRequestIpcEntity request = new();
        VpnConfig config = new(new VpnConfigParameters { PreferredProtocols = [] });
        VpnCredentials credentials = new(CreateKeyPair(), "username", "password");

        entityMapper.Map<VpnConfigIpcEntity, VpnConfig>(request.Config).Returns(config);
        entityMapper.Map<VpnServerIpcEntity, VpnHost>(request.Servers).Returns(new List<VpnHost>());
        entityMapper.Map<VpnCredentialsIpcEntity, VpnCredentials>(request.Credentials).Returns(credentials);

        VpnController subject = CreateVpnController(entityMapper, cache, stateMachine);

        await subject.Connect(request, CancellationToken.None);

        await cache.Received(1).ClearAsync(CancellationToken.None);
        await cache.DidNotReceiveWithAnyArgs().SetAsync(default, default);
        stateMachine.Received(1).Connect(Arg.Any<IReadOnlyList<VpnHost>>(), config, credentials);
    }

    [TestMethod]
    public async Task Connect_ShouldCacheLocalAgentTlsCredentials_WhenUsingCertificate()
    {
        IEntityMapper entityMapper = Substitute.For<IEntityMapper>();
        ILocalAgentTlsCredentialsCache cache = Substitute.For<ILocalAgentTlsCredentialsCache>();
        IVpnConnectionStateMachine stateMachine = Substitute.For<IVpnConnectionStateMachine>();
        ConnectionRequestIpcEntity request = new();
        VpnConfig config = new(new VpnConfigParameters { PreferredProtocols = [] });
        DateTime expirationDateUtc = DateTime.UtcNow.AddHours(1);
        AsymmetricKeyPair keyPair = CreateKeyPair();
        VpnCredentials credentials = new("certificate", expirationDateUtc, keyPair, string.Empty, string.Empty);

        entityMapper.Map<VpnConfigIpcEntity, VpnConfig>(request.Config).Returns(config);
        entityMapper.Map<VpnServerIpcEntity, VpnHost>(request.Servers).Returns(new List<VpnHost>());
        entityMapper.Map<VpnCredentialsIpcEntity, VpnCredentials>(request.Credentials).Returns(credentials);

        VpnController subject = CreateVpnController(entityMapper, cache, stateMachine);

        await subject.Connect(request, CancellationToken.None);

        await cache.Received(1).SetAsync(
            Arg.Is<LocalAgentTlsCredentials>(c =>
                c.ConnectionCertificate.Pem == credentials.ClientCertPem &&
                c.ConnectionCertificate.ExpirationDateUtc == credentials.ClientCertificateExpirationDateUtc &&
                c.ClientKeyPair == credentials.ClientKeyPair),
            CancellationToken.None);
        await cache.DidNotReceiveWithAnyArgs().ClearAsync(default);
        stateMachine.Received(1).Connect(Arg.Any<IReadOnlyList<VpnHost>>(), config, credentials);
    }

    [TestMethod]
    public async Task UpdateLocalAgentTlsCredentialsAsync_ShouldRejectMissingCertificate()
    {
        IEntityMapper entityMapper = Substitute.For<IEntityMapper>();
        ILocalAgentTlsCredentialsCache cache = Substitute.For<ILocalAgentTlsCredentialsCache>();
        LocalAgentTlsCredentialsIpcEntity ipcCredentials = new();
        LocalAgentTlsCredentials mappedCredentials = new(
            new ConnectionCertificate(string.Empty, DateTime.UtcNow.AddHours(1)),
            null);

        entityMapper
            .Map<LocalAgentTlsCredentialsIpcEntity, LocalAgentTlsCredentials>(ipcCredentials)
            .Returns(mappedCredentials);

        VpnController subject = CreateVpnController(entityMapper, cache);

        await subject.UpdateLocalAgentTlsCredentialsAsync(ipcCredentials, CancellationToken.None);

        await cache.DidNotReceiveWithAnyArgs().SetAsync(default, default);
    }

    [TestMethod]
    public async Task UpdateLocalAgentTlsCredentialsAsync_ShouldCacheValidCertificate()
    {
        IEntityMapper entityMapper = Substitute.For<IEntityMapper>();
        ILocalAgentTlsCredentialsCache cache = Substitute.For<ILocalAgentTlsCredentialsCache>();
        LocalAgentTlsCredentialsIpcEntity ipcCredentials = new();
        LocalAgentTlsCredentials mappedCredentials = new(
            new ConnectionCertificate("valid certificate", DateTime.UtcNow.AddHours(1)),
            null);

        entityMapper
            .Map<LocalAgentTlsCredentialsIpcEntity, LocalAgentTlsCredentials>(ipcCredentials)
            .Returns(mappedCredentials);

        VpnController subject = CreateVpnController(entityMapper, cache);

        await subject.UpdateLocalAgentTlsCredentialsAsync(ipcCredentials, CancellationToken.None);

        await cache.Received(1).SetAsync(mappedCredentials, CancellationToken.None);
    }

    private static AsymmetricKeyPair CreateKeyPair()
    {
        return new AsymmetricKeyPair(
            new SecretKey(new byte[] { 1, 2, 3 }, KeyAlgorithm.X25519),
            new CryptoPublicKey(new byte[] { 4, 5, 6 }, KeyAlgorithm.X25519));
    }

    private static VpnController CreateVpnController(
        IEntityMapper entityMapper,
        ILocalAgentTlsCredentialsCache cache)
    {
        return CreateVpnController(entityMapper, cache, Substitute.For<IVpnConnectionStateMachine>());
    }

    private static VpnController CreateVpnController(
        IEntityMapper entityMapper,
        ILocalAgentTlsCredentialsCache cache,
        IVpnConnectionStateMachine stateMachine)
    {
        return new VpnController(
            Substitute.For<ILogger>(),
            Substitute.For<IServiceSettings>(),
            Substitute.For<ITaskQueue>(),
            Substitute.For<IPortMappingProtocolClient>(),
            Substitute.For<IClientControllerSender>(),
            entityMapper,
            cache,
            Substitute.For<IControllerRetryManager>(),
            stateMachine,
            Substitute.For<ITunnelOrchestrator>(),
            Substitute.For<ILocalAgent>(),
            Substitute.For<ILocalAgentEventReceiver>(),
            Substitute.For<IServerHealthProbeService>());
    }
}
