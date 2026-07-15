// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Linq;
using Hyper5.Logging;

namespace Hyper5.HLE;

public sealed class Aerolib : ISymbolCatalog
{
    private static readonly Hyper5Logger Log = Hyper5Log.For("Aerolib");
    private static readonly Lazy<Aerolib> _instance = new(() => new Aerolib());
    private static readonly Aerolib EmptyCatalog = new Aerolib(empty: true);

    private Dictionary<string, SysAbiSymbol> _byNid;
    private Dictionary<string, SysAbiSymbol> _byExportName;

    public static Aerolib Instance => _instance.Value;

    private Aerolib(
        Dictionary<string, SysAbiSymbol> byNid,
        Dictionary<string, SysAbiSymbol> byExportName)
    {
        _byNid = byNid;
        _byExportName = byExportName;
    }

    private Aerolib()
    {
        _byNid = new Dictionary<string, SysAbiSymbol>(StringComparer.Ordinal);
        _byExportName = new Dictionary<string, SysAbiSymbol>(StringComparer.Ordinal);
        LoadFromEmbeddedBinary();
    }

    private Aerolib(bool empty)
    {
        _byNid = new Dictionary<string, SysAbiSymbol>(StringComparer.Ordinal);
        _byExportName = new Dictionary<string, SysAbiSymbol>(StringComparer.Ordinal);
    }

    public static ISymbolCatalog Empty => EmptyCatalog;

    // Salt appended to the export name before hashing; must match name2nid()
    // in scripts/generate_aerolib_binary.py.
    private static readonly byte[] NidSalt =
    {
        0x51, 0x8D, 0x64, 0xA6, 0x35, 0xDE, 0xD8, 0xC1,
        0xE6, 0xB0, 0x39, 0xB1, 0xC3, 0xE5, 0x52, 0x30,
    };

    /// <summary>
    /// Computes the NID for an export name: base64 of the byte-reversed first
    /// 8 bytes of SHA1(name + salt), '/' mapped to '-', padding stripped.
    /// Module export tables are keyed by NID, so name-based lookups
    /// (sceKernelDlsym, il2cpp API registration) must go through this even for
    /// symbols absent from the aerolib catalog.
    /// </summary>
    public static string DeriveNid(string exportName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(exportName);
        var payload = new byte[nameBytes.Length + NidSalt.Length];
        nameBytes.CopyTo(payload, 0);
        NidSalt.CopyTo(payload, nameBytes.Length);

        Span<byte> digest = stackalloc byte[20];
        _ = System.Security.Cryptography.SHA1.HashData(payload, digest);

        Span<byte> id = stackalloc byte[8];
        for (var i = 0; i < 8; i++)
        {
            id[i] = digest[7 - i];
        }

        return Convert.ToBase64String(id).TrimEnd('=').Replace('/', '-');
    }

    public string GetName(string nid)
    {
        if (string.IsNullOrEmpty(nid))
            return nid ?? string.Empty;

        if (_byNid.TryGetValue(nid, out var symbol))
            return symbol.ExportName;
        return nid;
    }

    public bool TryGetName(string nid, out string name)
    {
        if (string.IsNullOrEmpty(nid))
        {
            name = string.Empty;
            return false;
        }

        if (_byNid.TryGetValue(nid, out var symbol))
        {
            name = symbol.ExportName;
            return true;
        }
        name = string.Empty;
        return false;
    }

    public bool ContainsNid(string nid)
    {
        if (string.IsNullOrEmpty(nid))
            return false;

        return _byNid.ContainsKey(nid);
    }

    public Dictionary<string, string> GetAllNidNames()
    {
        var result = new Dictionary<string, string>(_byNid.Count, StringComparer.Ordinal);
        foreach (var kvp in _byNid)
        {
            result[kvp.Key] = kvp.Value.ExportName;
        }
        return result;
    }

    public int Count => _byNid.Count;

    public bool TryGetByNid(string nid, out SysAbiSymbol symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        return _byNid.TryGetValue(nid, out symbol);
    }

    public bool TryGetByExportName(string exportName, out SysAbiSymbol symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        return _byExportName.TryGetValue(exportName, out symbol);
    }

    private void LoadFromEmbeddedBinary()
    {
        try
        {
            var assembly = typeof(Aerolib).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("aerolib.bin", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                Log.Error("Embedded resource 'aerolib.bin' not found");
                return;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Log.Error("Failed to open embedded resource stream");
                return;
            }

            var data = new byte[stream.Length];
            stream.ReadExactly(data);

            int offset = 0;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;

            _byNid = new Dictionary<string, SysAbiSymbol>((int)count, StringComparer.Ordinal);
            _byExportName = new Dictionary<string, SysAbiSymbol>((int)count, StringComparer.Ordinal);

            for (uint i = 0; i < count; i++)
            {
                byte nidLen = data[offset++];
                string nid = System.Text.Encoding.UTF8.GetString(data, offset, nidLen);
                offset += nidLen;

                ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
                offset += 2;
                string name = System.Text.Encoding.UTF8.GetString(data, offset, nameLen);
                offset += nameLen;

                var symbol = new SysAbiSymbol(nid, name, name, Generation.Gen5);
                _byNid[nid] = symbol;
                _byExportName[name] = symbol;
            }

            Log.Info($"Loaded {_byNid.Count} NID entries from binary resource");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load embedded aerolib.bin: {ex.Message}", ex);
        }
    }

}
