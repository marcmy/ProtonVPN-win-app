/*
 * Copyright (c) 2025 Proton AG
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

namespace ProtonVPN.Vpn.Management;

/// <summary>
/// Collection of predefined messages to be sent to the OpenVPN management interface.
/// </summary>
public class ManagementMessages
{
    public ReceivedManagementMessage ReceivedMessage(string messageText)
    {
        return new ReceivedManagementMessage(messageText ?? "");
    }

    public ManagementMessage EchoOn()
    {
        return CreateMessage("echo on");
    }

    public ManagementMessage StateOn()
    {
        return CreateMessage("state on");
    }

    public ManagementMessage Bytecount()
    {
        return CreateMessage("bytecount 1");
    }

    public ManagementMessage LogOn()
    {
        return CreateMessage("log on");
    }

    public ManagementMessage HoldRelease()
    {
        return CreateMessage("hold release");
    }

    public ManagementMessage Username(string username)
    {
        return CreateSensitiveMessage(
            $"username 'Auth' {EscapedString(username)}",
            "username 'Auth' [...]");
    }

    public ManagementMessage Password(string password)
    {
        return CreateSensitiveMessage(
            $"password 'Auth' {EscapedString(password)}",
            "password 'Auth' [...]");
    }

    public ManagementMessage Disconnect()
    {
        return CreateMessage("signal SIGTERM");
    }

    public ManagementMessage Exit()
    {
        return CreateMessage("exit");
    }

    private static ManagementMessage CreateMessage(string messageText)
    {
        return new ManagementMessage(messageText, messageText);
    }

    private static ManagementMessage CreateSensitiveMessage(string messageText, string logText)
    {
        return new ManagementMessage(messageText, logText);
    }

    private static string EscapedString(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace(" ", "\\ ")
            + "\"";
    }
}
