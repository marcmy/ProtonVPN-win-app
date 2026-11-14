using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Common.Core.Helpers;

namespace ProtonVPN.Common.Core.Tests.Helpers;

[TestClass]
public class AssemblyVersionTest
{
    [TestMethod]
    public void GetDisplayVersion_UsesCurrentForkReleaseLabel()
    {
        Assert.AreEqual($"{AssemblyVersion.Get()}-marc-custom", AssemblyVersion.GetDisplayVersion());
    }
}
