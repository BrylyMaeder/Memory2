using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests;

[TestClass]
public sealed class LawfulCollapsedDomainTests
{
    [TestMethod]
    public void Generated_domain_reconstructs_native_values_and_stores_only_exceptions()
    {
        static long Law(long coordinate) => unchecked(coordinate * 6364136223846793005L + 1442695040888963407L);
        var domain = new LawfulCollapsedDomain<long>(1_000_000_000L, Law, 4);

        domain.SetSorted(10, 42);
        domain.SetSorted(1_000_000, 84);

        Assert.AreEqual(Law(9), domain.Read(9));
        Assert.AreEqual(42L, domain.Read(10));
        Assert.AreEqual(84L, domain.Read(1_000_000));
        Assert.AreEqual(2, domain.ExceptionCount);

        var estimate = LawfulCollapse.Estimate(domain, sizeof(long));
        Assert.IsTrue(estimate.BytesPerLogicalRecord < 0.001);
        Assert.IsTrue(estimate.CollapseRatio > 1_000);

        domain.Set(10, Law(10));
        Assert.AreEqual(1, domain.ExceptionCount);
        Assert.AreEqual(Law(10), domain.Read(10));
    }
}
