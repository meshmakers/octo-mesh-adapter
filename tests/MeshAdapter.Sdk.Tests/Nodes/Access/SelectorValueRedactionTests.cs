using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using static MeshAdapter.Sdk.Tests.Helpers.AttributeAccessTestModel;

namespace MeshAdapter.Sdk.Tests.Nodes.Access;

/// <summary>
///     AB#8077 — <see cref="RtAttributeAccessGuard.WithoutSelectorValues" /> removes the whole value of an entity selector
///     from the path an access error shows, also when the selector is never closed or its value contains <c>]</c>; the
///     same rule as the asset repository's <c>HiddenAttributeAccessException.RedactSelectorValues</c> (AB#8052).
/// </summary>
public class SelectorValueRedactionTests
{
    private const string Guess = "dummy-guess-0001";

    private readonly Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService _cache = Cache();

    [Theory]
    [InlineData("members.someType[passwordHash='AQAA']->name", "members.someType[passwordHash=…]->name")]
    [InlineData("a.t[x=1].b.u[y='z']->c", "a.t[x=…]->c")]
    [InlineData("items[0].name", "items[0].name")]
    [InlineData("name", "name")]
    [InlineData("items[0].t[passwordHash='AQAA']->name", "items[0].t[passwordHash=…]->name")]
    [InlineData("a.t[x=1]::target", "a.t[x=…]::target")]
    [InlineData("a.t[x=1].b", "a.t[x=…].b")]
    [InlineData("a.t[x=1]", "a.t[x=…]")]
    public void WellFormedPaths_KeepTheirStructure_WithoutValues(string path, string expected)
    {
        Assert.Equal(expected, RtAttributeAccessGuard.WithoutSelectorValues(path));
    }

    [Theory]
    // a value containing ']' (or '[') whose path still ends with a valid suffix
    [InlineData("members.t[passwordHash='AQ]AA-secret']->name", "members.t[passwordHash=…]->name")]
    [InlineData("members.t[passwordHash='AQ]AA]-secret']->name", "members.t[passwordHash=…]->name")]
    [InlineData("members.t[passwordHash='AQ[AA]-secret']->name", "members.t[passwordHash=…]->name")]
    [InlineData("members.t[passwordHash=AQ]AA-secret]", "members.t[passwordHash=…]")]
    [InlineData("members.t[passwordHash='AQ]AA::secret[x]", "members.t[passwordHash=…]")]
    // an unclosed selector: used to be returned unchanged
    [InlineData("members.t[passwordHash='AQAA-secret", "members.t[passwordHash=…")]
    [InlineData("members.t[name=AQAA-secret->passwordHash", "members.t[name=…")]
    // the text after the last ']' is no valid path suffix: it belongs to the value, used to be shown
    [InlineData("members.t[passwordHash='AQAA-secret]'", "members.t[passwordHash=…")]
    [InlineData("members.t[passwordHash='AQ]AA-secret", "members.t[passwordHash=…")]
    [InlineData("members.t[passwordHash='AQ]AA.secret", "members.t[passwordHash=…")]
    [InlineData("members.t[passwordHash='AQ]->AA=secret", "members.t[passwordHash=…")]
    public void TheWholeSelectorValue_IsRemoved_WhateverBracketsItContains(string path, string expected)
    {
        var redacted = RtAttributeAccessGuard.WithoutSelectorValues(path);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("AQ", redacted);
        Assert.DoesNotContain("AA", redacted);
        Assert.DoesNotContain("secret", redacted);
    }

    [Theory]
    // hidden selector key, value with ']' followed by text that is no path suffix
    [InlineData("members.Test/Account[passwordHash=x]" + Guess)]
    // unclosed selector, refused by its hidden trailing attribute
    [InlineData("members.Test/Account[name=" + Guess + "->passwordHash")]
    public void EnsureNotHidden_TheRefusal_EchoesNoPartOfTheValue(string path)
    {
        var ex = Assert.Throws<RtAttributeHiddenException>(() =>
            RtAttributeAccessGuard.EnsureNotHidden(_cache, Tenant, "$.nodes[0]", [AccountType], [path],
                "field filter"));

        Assert.DoesNotContain(Guess, ex.Message);
        Assert.DoesNotContain(Guess, ex.AttributePath);
    }
}
