// Copyright (C) 2026 Hyper5 Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Hyper5.HLE;
using Xunit;

namespace Hyper5.Libs.Tests;

public class AerolibNidTests
{
    // Expected values computed with name2nid() in scripts/generate_aerolib_binary.py,
    // which matches the console's NID derivation. If DeriveNid drifts from the
    // Python implementation, name-based sceKernelDlsym resolution silently breaks.
    [Theory]
    [InlineData("sceKernelDlsym", "LwG8g3niqwA")]
    [InlineData("sceKernelCreateSema", "188x57JYp0g")]
    [InlineData("il2cpp_init", "vXRp9zVGPzU")]
    [InlineData("module_start", "BaOKcng8g88")]
    [InlineData("scriptingGetMem", "ayuoL6Vjz2k")]
    [InlineData("scriptingFreeMem", "yV45DG6ei28")]
    [InlineData("scriptingRealloc", "RmgMXAG-sdA")]
    [InlineData("scriptingCalloc", "YPr-sOQsrEA")]
    [InlineData("malloc", "gQX+4GDQjpM")]
    [InlineData("free", "tIhsqj0qsFE")]
    [InlineData("realloc", "Y7aJ1uydPMo")]
    [InlineData("calloc", "2X5agFjKxMc")]
    public void DeriveNid_MatchesAerolibCatalogDerivation(string name, string expectedNid)
    {
        Assert.Equal(expectedNid, Aerolib.DeriveNid(name));
    }

    [Fact]
    public void DeriveNid_RejectsEmptyNames()
    {
        Assert.ThrowsAny<ArgumentException>(() => Aerolib.DeriveNid(""));
        Assert.ThrowsAny<ArgumentException>(() => Aerolib.DeriveNid("   "));
    }

    [Fact]
    public void DeriveNid_AgreesWithEmbeddedCatalog()
    {
        // Every catalog entry was generated with the Python hash; spot-check a
        // large slice against the C# implementation end to end.
        var checkedCount = 0;
        foreach (var (nid, name) in Aerolib.Instance.GetAllNidNames())
        {
            Assert.Equal(nid, Aerolib.DeriveNid(name));
            if (++checkedCount >= 500)
            {
                break;
            }
        }

        Assert.True(checkedCount > 0);
    }
}
