using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace MeshAdapter.Sdk.Tests.Common;

public class PlatformOnlySecretsTests
{
    [Theory]
    [InlineData("System.Communication/SecretDefault")]
    public void IsPlatformOnly_SecretDefaultType_IsTrue(string ckTypeId)
    {
        Assert.True(PlatformOnlySecrets.IsPlatformOnly((RtCkId<CkTypeId>)ckTypeId));
    }

    [Theory]
    [InlineData("System.Communication/Application")]
    [InlineData("System.Communication/EMailSenderConfiguration")]
    [InlineData("Other.Model/SecretDefault")]
    [InlineData("System.Communication/SecretDefaultX")]
    public void IsPlatformOnly_OtherTypes_IsFalse(string ckTypeId)
    {
        Assert.False(PlatformOnlySecrets.IsPlatformOnly((RtCkId<CkTypeId>)ckTypeId));
    }

    [Fact]
    public void IsPlatformOnly_Null_IsFalse()
    {
        Assert.False(PlatformOnlySecrets.IsPlatformOnly(null));
    }

    [Theory]
    [InlineData("System.Communication/SecretDefault")]
    [InlineData("System.Communication-4.15.0/SecretDefault")]
    [InlineData("  System.Communication/SecretDefault ")]
    [InlineData("system.communication/secretdefault")]
    [InlineData("system.communication-4.15.0/secretdefault")]
    [InlineData("SYSTEM.COMMUNICATION/SECRETDEFAULT")]
    public void IsPlatformOnlyCkTypeId_Text_MatchesTheTypeWithOrWithoutModelVersion(string text)
    {
        Assert.True(PlatformOnlySecrets.IsPlatformOnlyCkTypeId(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ck-type-id")]
    [InlineData("System.Communication/Application")]
    [InlineData("System.Communication-4.15.0/Application")]
    [InlineData("System.Communication/SecretDefaultX")]
    public void IsPlatformOnlyCkTypeId_UnrelatedOrMalformedText_IsFalse(string? text)
    {
        Assert.False(PlatformOnlySecrets.IsPlatformOnlyCkTypeId(text));
    }

    [Theory]
    [InlineData("System.Communication/Application", "SecretDefaults", true)]
    [InlineData("System.Communication/Application", "secretdefaults", true)]
    [InlineData("System.Communication/Application", " SecretDefaults ", true)]
    [InlineData("System.Communication/Application", "SecretDefaults.SecretValue", true)]
    [InlineData("System.Communication/Application", "SecretDefaults[3].SecretValue", true)]
    [InlineData("System.Communication/Application", "ValueOverrides", false)]
    [InlineData("System.Communication/Application", "SecretDefaultsX", false)]
    [InlineData("System.Communication/EMailSenderConfiguration", "SecretDefaults", false)]
    public void IsPlatformOnlyAttribute_MatchesTheSecretDefaultsMemberOfTheApplication(string ckTypeId,
        string attributeName, bool expected)
    {
        Assert.Equal(expected, PlatformOnlySecrets.IsPlatformOnlyAttribute((RtCkId<CkTypeId>)ckTypeId, attributeName));
    }

    [Fact]
    public void IsPlatformOnly_WithCache_AnswersByIdWhenTheTypeIsNotCached()
    {
        var cache = A.Fake<ICkCacheService>();
        var unknown = (RtCkId<CkTypeId>)"System.Communication/Unknown";

        Assert.False(PlatformOnlySecrets.IsPlatformOnly(cache, "t", unknown));
        Assert.True(PlatformOnlySecrets.IsPlatformOnly(cache, "t", (RtCkId<CkTypeId>)"System.Communication/SecretDefault"));
    }
}
