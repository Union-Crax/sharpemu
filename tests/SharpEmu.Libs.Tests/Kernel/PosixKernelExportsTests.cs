// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// KernelMemoryCompatExports fd/mapping tables are shared static state; run these
// serially in one collection so parallel classes never race the tables.
[Collection("PosixKernelExports")]
public sealed class PosixKernelExportsTests : IDisposable
{
    private const ulong GuestBase = 0x1_0000_0000;
    private const ulong PathAddress = GuestBase + 0x100;
    private const ulong SecondPathAddress = GuestBase + 0x300;
    private const ulong BufferAddress = GuestBase + 0x1000;
    private const ulong IoVecAddress = GuestBase + 0x2000;
    private const ulong StructAddress = GuestBase + 0x3000;
    private const ulong StackAddress = GuestBase + 0x8000;

    private readonly FakeGuestAddressSpaceMemory _memory = new(GuestBase, 0x40000);
    private readonly CpuContext _ctx;
    private readonly string _tempRoot;

    public PosixKernelExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _tempRoot = Directory.CreateTempSubdirectory("sharpemu-posix-tests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreateHostFile(string name, byte[] content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private int OpenGuestFile(string hostPath, int flags)
    {
        _memory.WriteCString(PathAddress, hostPath);
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)flags);
        var result = KernelMemoryCompatExports.KernelOpenUnderscore(_ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        return unchecked((int)_ctx[CpuRegister.Rax]);
    }

    private void CloseGuestFile(int fd)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelClose(_ctx));
    }

    [Fact]
    public void Pread_ReadsAtOffsetWithoutMovingFileCursor()
    {
        var hostPath = CreateHostFile("pread.bin", Encoding.ASCII.GetBytes("0123456789ABCDEF"));
        var fd = OpenGuestFile(hostPath, flags: 0);
        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = BufferAddress;
            _ctx[CpuRegister.Rdx] = 5;
            _ctx[CpuRegister.Rcx] = 7;
            Assert.Equal(0, KernelMemoryCompatExports.PosixPread(_ctx));
            Assert.Equal(5UL, _ctx[CpuRegister.Rax]);

            Span<byte> readBack = stackalloc byte[5];
            Assert.True(_memory.TryRead(BufferAddress, readBack));
            Assert.Equal("789AB", Encoding.ASCII.GetString(readBack));

            // The descriptor's sequential cursor must still be at 0.
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = BufferAddress + 0x100;
            _ctx[CpuRegister.Rdx] = 4;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelReadUnderscore(_ctx));
            Assert.Equal(4UL, _ctx[CpuRegister.Rax]);
            Assert.True(_memory.TryRead(BufferAddress + 0x100, readBack[..4]));
            Assert.Equal("0123", Encoding.ASCII.GetString(readBack[..4]));
        }
        finally
        {
            CloseGuestFile(fd);
        }
    }

    [Fact]
    public void Pread_BadDescriptor_FailsWithMinusOne()
    {
        _ctx[CpuRegister.Rdi] = 0x7FFF;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = 4;
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixPread(_ctx));
        Assert.Equal(ulong.MaxValue, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Pwrite_WritesAtOffset()
    {
        var hostPath = CreateHostFile("pwrite.bin", Encoding.ASCII.GetBytes("0123456789"));
        var fd = OpenGuestFile(hostPath, flags: 0x2 /* O_RDWR */);
        try
        {
            _memory.TryWrite(BufferAddress, "XY"u8);
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = BufferAddress;
            _ctx[CpuRegister.Rdx] = 2;
            _ctx[CpuRegister.Rcx] = 3;
            Assert.Equal(0, KernelMemoryCompatExports.PosixPwrite(_ctx));
            Assert.Equal(2UL, _ctx[CpuRegister.Rax]);
        }
        finally
        {
            CloseGuestFile(fd);
        }

        Assert.Equal("012XY56789", File.ReadAllText(hostPath));
    }

    [Fact]
    public void Ftruncate_ShrinksOpenFile()
    {
        var hostPath = CreateHostFile("truncate.bin", Encoding.ASCII.GetBytes("0123456789"));
        var fd = OpenGuestFile(hostPath, flags: 0x2 /* O_RDWR */);
        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = 4;
            Assert.Equal(0, KernelMemoryCompatExports.PosixFtruncate(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        }
        finally
        {
            CloseGuestFile(fd);
        }

        Assert.Equal("0123", File.ReadAllText(hostPath));
    }

    [Fact]
    public void Fsync_SucceedsOnOpenFile_FailsOnBadDescriptor()
    {
        var hostPath = CreateHostFile("fsync.bin", Encoding.ASCII.GetBytes("data"));
        var fd = OpenGuestFile(hostPath, flags: 0x2 /* O_RDWR */);
        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            Assert.Equal(0, KernelMemoryCompatExports.PosixFsync(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        }
        finally
        {
            CloseGuestFile(fd);
        }

        _ctx[CpuRegister.Rdi] = 0x7FFF;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixFsync(_ctx));
        Assert.Equal(ulong.MaxValue, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Access_ReportsExistingAndMissingPaths()
    {
        var hostPath = CreateHostFile("access.bin", [1, 2, 3]);
        _memory.WriteCString(PathAddress, hostPath);
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0; // F_OK
        Assert.Equal(0, KernelMemoryCompatExports.PosixAccess(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        _memory.WriteCString(PathAddress, Path.Combine(_tempRoot, "missing.bin"));
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixAccess(_ctx));
        Assert.Equal(ulong.MaxValue, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Rename_MovesFileAndOverwritesDestination()
    {
        var fromPath = CreateHostFile("rename-from.bin", Encoding.ASCII.GetBytes("new-content"));
        var toPath = CreateHostFile("rename-to.bin", Encoding.ASCII.GetBytes("old"));

        _memory.WriteCString(PathAddress, fromPath);
        _memory.WriteCString(SecondPathAddress, toPath);
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = SecondPathAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixRename(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        Assert.False(File.Exists(fromPath));
        Assert.Equal("new-content", File.ReadAllText(toPath));
    }

    [Fact]
    public void SceKernelRename_MissingSource_ReturnsNotFound()
    {
        _memory.WriteCString(PathAddress, Path.Combine(_tempRoot, "does-not-exist.bin"));
        _memory.WriteCString(SecondPathAddress, Path.Combine(_tempRoot, "target.bin"));
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = SecondPathAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelMemoryCompatExports.KernelRename(_ctx));
    }

    [Fact]
    public void PosixMkdirRmdir_RoundTripWithErrnoStyleFailures()
    {
        var dirPath = Path.Combine(_tempRoot, "newdir");
        _memory.WriteCString(PathAddress, dirPath);
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0x1FF; // 0777
        Assert.Equal(0, KernelMemoryCompatExports.PosixMkdir(_ctx));
        Assert.True(Directory.Exists(dirPath));

        // Second mkdir on the same path fails posix-style.
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0x1FF;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixMkdir(_ctx));
        Assert.Equal(ulong.MaxValue, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = PathAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixRmdir(_ctx));
        Assert.False(Directory.Exists(dirPath));
    }

    [Fact]
    public void PosixUnlink_RemovesFile()
    {
        var hostPath = CreateHostFile("unlink.bin", [1]);
        _memory.WriteCString(PathAddress, hostPath);
        _ctx[CpuRegister.Rdi] = PathAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixUnlink(_ctx));
        Assert.False(File.Exists(hostPath));
    }

    [Fact]
    public void Writev_GathersBuffersIntoFile()
    {
        var hostPath = CreateHostFile("writev.bin", []);
        var fd = OpenGuestFile(hostPath, flags: 0x2 /* O_RDWR */);
        try
        {
            _memory.TryWrite(BufferAddress, "Hello, "u8);
            _memory.TryWrite(BufferAddress + 0x100, "world!"u8);
            WriteIoVector(IoVecAddress, 0, BufferAddress, 7);
            WriteIoVector(IoVecAddress, 1, BufferAddress + 0x100, 6);

            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = IoVecAddress;
            _ctx[CpuRegister.Rdx] = 2;
            Assert.Equal(0, KernelMemoryCompatExports.PosixWritev(_ctx));
            Assert.Equal(13UL, _ctx[CpuRegister.Rax]);
        }
        finally
        {
            CloseGuestFile(fd);
        }

        Assert.Equal("Hello, world!", File.ReadAllText(hostPath));
    }

    [Fact]
    public void Readv_ScattersFileIntoBuffers()
    {
        var hostPath = CreateHostFile("readv.bin", Encoding.ASCII.GetBytes("0123456789"));
        var fd = OpenGuestFile(hostPath, flags: 0);
        try
        {
            WriteIoVector(IoVecAddress, 0, BufferAddress, 4);
            WriteIoVector(IoVecAddress, 1, BufferAddress + 0x100, 6);

            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = IoVecAddress;
            _ctx[CpuRegister.Rdx] = 2;
            Assert.Equal(0, KernelMemoryCompatExports.PosixReadv(_ctx));
            Assert.Equal(10UL, _ctx[CpuRegister.Rax]);

            Span<byte> first = stackalloc byte[4];
            Span<byte> second = stackalloc byte[6];
            Assert.True(_memory.TryRead(BufferAddress, first));
            Assert.True(_memory.TryRead(BufferAddress + 0x100, second));
            Assert.Equal("0123", Encoding.ASCII.GetString(first));
            Assert.Equal("456789", Encoding.ASCII.GetString(second));
        }
        finally
        {
            CloseGuestFile(fd);
        }
    }

    [Fact]
    public void Mmap_AnonymousMappingIsUsableAndMunmapSucceeds()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0x4000;
        _ctx[CpuRegister.Rdx] = 0x3; // PROT_READ | PROT_WRITE
        _ctx[CpuRegister.Rcx] = 0x1002; // MAP_ANON | MAP_PRIVATE
        _ctx[CpuRegister.R8] = unchecked((ulong)-1L);
        _ctx[CpuRegister.R9] = 0;
        Assert.Equal(0, KernelMemoryCompatExports.PosixMmap(_ctx));

        var mapped = _ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, mapped);
        Assert.NotEqual(ulong.MaxValue, mapped);
        Assert.True(_memory.TryWrite(mapped, [0xAB]));

        _ctx[CpuRegister.Rdi] = mapped;
        _ctx[CpuRegister.Rsi] = 0x4000;
        Assert.Equal(0, KernelMemoryCompatExports.PosixMunmap(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Mmap_FileBackedMappingCopiesContentsFromOffset()
    {
        var hostPath = CreateHostFile("mmap.bin", Encoding.ASCII.GetBytes("0123456789ABCDEF"));
        var fd = OpenGuestFile(hostPath, flags: 0);
        ulong mapped = 0;
        try
        {
            _ctx[CpuRegister.Rdi] = 0;
            _ctx[CpuRegister.Rsi] = 8;
            _ctx[CpuRegister.Rdx] = 0x1; // PROT_READ
            _ctx[CpuRegister.Rcx] = 0x2; // MAP_PRIVATE
            _ctx[CpuRegister.R8] = unchecked((ulong)fd);
            _ctx[CpuRegister.R9] = 4;
            Assert.Equal(0, KernelMemoryCompatExports.PosixMmap(_ctx));

            mapped = _ctx[CpuRegister.Rax];
            Assert.NotEqual(0UL, mapped);
            Span<byte> content = stackalloc byte[8];
            Assert.True(_memory.TryRead(mapped, content));
            Assert.Equal("456789AB", Encoding.ASCII.GetString(content));
        }
        finally
        {
            if (mapped != 0)
            {
                _ctx[CpuRegister.Rdi] = mapped;
                _ctx[CpuRegister.Rsi] = 8;
                KernelMemoryCompatExports.PosixMunmap(_ctx);
            }

            CloseGuestFile(fd);
        }
    }

    [Fact]
    public void SceKernelMmap_WritesMappedAddressThroughStackArgument()
    {
        // void** res is the 7th argument: it lives at [rsp + 8].
        const ulong ResultSlot = StructAddress + 0x40;
        _ctx[CpuRegister.Rsp] = StackAddress;
        Span<byte> resultPointer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(resultPointer, ResultSlot);
        Assert.True(_memory.TryWrite(StackAddress + 8, resultPointer));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0x2000;
        _ctx[CpuRegister.Rdx] = 0x3;
        _ctx[CpuRegister.Rcx] = 0x1002; // MAP_ANON | MAP_PRIVATE
        _ctx[CpuRegister.R8] = unchecked((ulong)-1L);
        _ctx[CpuRegister.R9] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelMmap(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        Assert.True(_ctx.TryReadUInt64(ResultSlot, out var mapped));
        Assert.NotEqual(0UL, mapped);

        _ctx[CpuRegister.Rdi] = mapped;
        _ctx[CpuRegister.Rsi] = 0x2000;
        Assert.Equal(0, KernelMemoryCompatExports.PosixMunmap(_ctx));
    }

    [Fact]
    public void Sigprocmask_WritesEmptyOldMaskAndValidatesHow()
    {
        // Poison the old-mask slot to prove it gets cleared.
        Span<byte> poison = stackalloc byte[16];
        poison.Fill(0xFF);
        Assert.True(_memory.TryWrite(StructAddress, poison));

        _ctx[CpuRegister.Rdi] = 1; // SIG_BLOCK
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = StructAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixSigprocmask(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        Span<byte> oldMask = stackalloc byte[16];
        Assert.True(_memory.TryRead(StructAddress, oldMask));
        Assert.True(oldMask.IndexOfAnyExcept((byte)0) < 0);

        _ctx[CpuRegister.Rdi] = 9; // invalid how
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixSigprocmask(_ctx));
        Assert.Equal(ulong.MaxValue, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Getpagesize_ReportsOrbisPageSize()
    {
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.PosixGetpagesize(_ctx));
        Assert.Equal(0x4000UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void IsProspero_ReportsTrue()
    {
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelIsProspero(_ctx));
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetSystemSwVersion_FillsSizeStringAndVersion()
    {
        Span<byte> sizeField = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeField, 0x28);
        Assert.True(_memory.TryWrite(StructAddress, sizeField));

        _ctx[CpuRegister.Rdi] = StructAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelGetSystemSwVersion(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        Span<byte> payload = stackalloc byte[0x28];
        Assert.True(_memory.TryRead(StructAddress, payload));
        Assert.Equal(0x28UL, BinaryPrimitives.ReadUInt64LittleEndian(payload));
        var versionString = Encoding.ASCII.GetString(payload.Slice(8, 28)).TrimEnd('\0');
        Assert.Equal("9.008.001", versionString);
        Assert.Equal(0x09008001u, BinaryPrimitives.ReadUInt32LittleEndian(payload[0x24..]));
    }

    [Fact]
    public void PthreadDetach_PosixFlavorReturnsErrnoValues()
    {
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(3, KernelPthreadExtendedCompatExports.PosixPthreadDetach(_ctx));
        Assert.Equal(3UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = 0xDEAD_BEEF;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadDetach(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Dup_SharesFileOffsetAndSurvivesCloseOfOriginal()
    {
        var hostPath = CreateHostFile("dup.bin", Encoding.ASCII.GetBytes("0123456789"));
        var fd = OpenGuestFile(hostPath, flags: 0);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.PosixDup(_ctx));
        var dupFd = unchecked((int)_ctx[CpuRegister.Rax]);
        Assert.NotEqual(fd, dupFd);

        // Reading via the original advances the shared offset.
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = 4;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelReadUnderscore(_ctx));

        CloseGuestFile(fd);

        // The duplicate still works and continues at the shared offset.
        _ctx[CpuRegister.Rdi] = unchecked((ulong)dupFd);
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = 4;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelReadUnderscore(_ctx));
        Assert.Equal(4UL, _ctx[CpuRegister.Rax]);
        Span<byte> readBack = stackalloc byte[4];
        Assert.True(_memory.TryRead(BufferAddress, readBack));
        Assert.Equal("4567", Encoding.ASCII.GetString(readBack));

        CloseGuestFile(dupFd);
    }

    [Fact]
    public void Dup2_RedirectsTargetDescriptor()
    {
        var firstPath = CreateHostFile("dup2-a.bin", Encoding.ASCII.GetBytes("AAAA"));
        var secondPath = CreateHostFile("dup2-b.bin", Encoding.ASCII.GetBytes("BBBB"));
        var firstFd = OpenGuestFile(firstPath, flags: 0);
        var secondFd = OpenGuestFile(secondPath, flags: 0);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)firstFd);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)secondFd);
        Assert.Equal(0, KernelMemoryCompatExports.PosixDup2(_ctx));
        Assert.Equal(unchecked((ulong)secondFd), _ctx[CpuRegister.Rax]);

        // secondFd now reads the first file's contents.
        _ctx[CpuRegister.Rdi] = unchecked((ulong)secondFd);
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = 4;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelReadUnderscore(_ctx));
        Span<byte> readBack = stackalloc byte[4];
        Assert.True(_memory.TryRead(BufferAddress, readBack));
        Assert.Equal("AAAA", Encoding.ASCII.GetString(readBack));

        CloseGuestFile(secondFd);
        CloseGuestFile(firstFd);
    }

    [Fact]
    public void Fcntl_SupportsDupfdAndGetflAndRejectsUnknownCommands()
    {
        var hostPath = CreateHostFile("fcntl.bin", Encoding.ASCII.GetBytes("data"));
        var fd = OpenGuestFile(hostPath, flags: 0x2 /* O_RDWR */);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = 0; // F_DUPFD
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelMemoryCompatExports.PosixFcntl(_ctx));
        var dupFd = unchecked((int)_ctx[CpuRegister.Rax]);
        Assert.NotEqual(fd, dupFd);
        CloseGuestFile(dupFd);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = 3; // F_GETFL
        Assert.Equal(0, KernelMemoryCompatExports.PosixFcntl(_ctx));
        Assert.Equal(0x2UL, _ctx[CpuRegister.Rax]); // O_RDWR

        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = 999;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixFcntl(_ctx));

        CloseGuestFile(fd);
    }

    [Fact]
    public void ChdirGetcwd_RoundTripsAndReportsErange()
    {
        var dirPath = Path.Combine(_tempRoot, "cwd-target");
        Directory.CreateDirectory(dirPath);

        _memory.WriteCString(PathAddress, dirPath);
        _ctx[CpuRegister.Rdi] = PathAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixChdir(_ctx));

        _ctx[CpuRegister.Rdi] = BufferAddress;
        _ctx[CpuRegister.Rsi] = 0x400;
        Assert.Equal(0, KernelMemoryCompatExports.PosixGetcwd(_ctx));
        Assert.Equal(BufferAddress, _ctx[CpuRegister.Rax]);

        var expected = "/" + string.Join('/', dirPath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        Span<byte> readBack = stackalloc byte[expected.Length];
        Assert.True(_memory.TryRead(BufferAddress, readBack));
        Assert.Equal(expected, Encoding.UTF8.GetString(readBack));

        _ctx[CpuRegister.Rdi] = BufferAddress;
        _ctx[CpuRegister.Rsi] = 2; // far too small
        Assert.Equal(-1, KernelMemoryCompatExports.PosixGetcwd(_ctx));
    }

    [Fact]
    public void Sysconf_ReportsPageSizeAndRejectsUnknownNames()
    {
        _ctx[CpuRegister.Rdi] = 47; // _SC_PAGESIZE
        Assert.Equal(0, KernelMemoryCompatExports.PosixSysconf(_ctx));
        Assert.Equal(0x4000UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = 9999;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixSysconf(_ctx));
        Assert.Equal(ulong.MaxValue, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Getrlimit_WritesInfiniteLimits()
    {
        _ctx[CpuRegister.Rdi] = 8; // RLIMIT_NOFILE
        _ctx[CpuRegister.Rsi] = StructAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixGetrlimit(_ctx));

        Span<byte> payload = stackalloc byte[16];
        Assert.True(_memory.TryRead(StructAddress, payload));
        Assert.Equal((ulong)long.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(payload));
        Assert.Equal((ulong)long.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]));
    }

    [Fact]
    public void Sigaction_ZeroesOldActionAndValidatesSignal()
    {
        Span<byte> poison = stackalloc byte[32];
        poison.Fill(0xFF);
        Assert.True(_memory.TryWrite(StructAddress, poison));

        _ctx[CpuRegister.Rdi] = 11; // SIGSEGV
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = StructAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixSigaction(_ctx));

        Span<byte> oldAction = stackalloc byte[32];
        Assert.True(_memory.TryRead(StructAddress, oldAction));
        Assert.True(oldAction.IndexOfAnyExcept((byte)0) < 0);

        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixSigaction(_ctx));
    }

    [Fact]
    public void ClockGetres_WritesResolutionForKnownClock()
    {
        _ctx[CpuRegister.Rdi] = 0; // CLOCK_REALTIME
        _ctx[CpuRegister.Rsi] = StructAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixClockGetres(_ctx));

        Span<byte> payload = stackalloc byte[16];
        Assert.True(_memory.TryRead(StructAddress, payload));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(payload));
        Assert.Equal(100L, BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));
    }

    [Fact]
    public void Poll_MarksFileDescriptorsReadyAndFlagsBadOnes()
    {
        var hostPath = CreateHostFile("poll.bin", Encoding.ASCII.GetBytes("x"));
        var fd = OpenGuestFile(hostPath, flags: 0);

        // Entry 0: real file, POLLIN|POLLOUT. Entry 1: bogus fd. Entry 2: fd -1.
        WritePollFd(StructAddress, 0, fd, 0x0005);
        WritePollFd(StructAddress, 1, 0x7000, 0x0001);
        WritePollFd(StructAddress, 2, -1, 0x0001);

        _ctx[CpuRegister.Rdi] = StructAddress;
        _ctx[CpuRegister.Rsi] = 3;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelMemoryCompatExports.PosixPoll(_ctx));
        Assert.Equal(2UL, _ctx[CpuRegister.Rax]);

        Assert.Equal(0x0005, ReadPollRevents(StructAddress, 0));
        Assert.Equal(0x0020, ReadPollRevents(StructAddress, 1)); // POLLNVAL
        Assert.Equal(0, ReadPollRevents(StructAddress, 2));

        CloseGuestFile(fd);
    }

    [Fact]
    public void Umask_ReturnsPreviousMask()
    {
        _ctx[CpuRegister.Rdi] = 0x17; // 027
        Assert.Equal(0, KernelMemoryCompatExports.PosixUmask(_ctx));
        var previous = _ctx[CpuRegister.Rax];

        _ctx[CpuRegister.Rdi] = previous;
        Assert.Equal(0, KernelMemoryCompatExports.PosixUmask(_ctx));
        Assert.Equal(0x17UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Realpath_CollapsesDotSegments()
    {
        var subDir = Path.Combine(_tempRoot, "sub");
        Directory.CreateDirectory(subDir);
        var filePath = CreateHostFile("real.bin", [1]);

        _memory.WriteCString(PathAddress, $"{_tempRoot}/sub/.././real.bin");
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixRealpath(_ctx));
        Assert.Equal(BufferAddress, _ctx[CpuRegister.Rax]);

        var expected = "/" + string.Join('/', filePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        Span<byte> readBack = stackalloc byte[expected.Length];
        Assert.True(_memory.TryRead(BufferAddress, readBack));
        Assert.Equal(expected, Encoding.UTF8.GetString(readBack));
    }

    [Fact]
    public void ShmOpen_CreatesReopensAndUnlinks()
    {
        var name = $"/sharpemu-test-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _memory.WriteCString(PathAddress, name);
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0x0202; // O_CREAT | O_RDWR
        _ctx[CpuRegister.Rdx] = 0x1B6;  // 0666
        Assert.Equal(0, KernelMemoryCompatExports.PosixShmOpen(_ctx));
        var fd = unchecked((int)_ctx[CpuRegister.Rax]);
        Assert.True(fd > 2);
        CloseGuestFile(fd);

        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0x2; // O_RDWR, no O_CREAT: must already exist
        Assert.Equal(0, KernelMemoryCompatExports.PosixShmOpen(_ctx));
        CloseGuestFile(unchecked((int)_ctx[CpuRegister.Rax]));

        _ctx[CpuRegister.Rdi] = PathAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixShmUnlink(_ctx));

        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0x2;
        Assert.Equal(-1, KernelMemoryCompatExports.PosixShmOpen(_ctx));
    }

    [Fact]
    public void Utimes_SetsFileTimesFromTimevals()
    {
        var hostPath = CreateHostFile("utimes.bin", [1]);
        _memory.WriteCString(PathAddress, hostPath);

        Span<byte> times = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(times, 1_000_000_000);      // atime sec
        BinaryPrimitives.WriteUInt64LittleEndian(times[8..], 0);             // atime usec
        BinaryPrimitives.WriteUInt64LittleEndian(times[16..], 1_500_000_000); // mtime sec
        BinaryPrimitives.WriteUInt64LittleEndian(times[24..], 500_000);       // mtime usec
        Assert.True(_memory.TryWrite(StructAddress, times));

        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = StructAddress;
        Assert.Equal(0, KernelMemoryCompatExports.PosixUtimes(_ctx));

        var expected = DateTime.UnixEpoch.AddSeconds(1_500_000_000).AddTicks(500_000 * 10);
        Assert.Equal(expected, File.GetLastWriteTimeUtc(hostPath));
    }

    [Fact]
    public void ProcessIdentityStubsReportUnprivilegedValues()
    {
        Assert.Equal(0, KernelMemoryCompatExports.PosixGetuid(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0, KernelMemoryCompatExports.PosixIssetugid(_ctx));
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0, KernelMemoryCompatExports.PosixGetppid(_ctx));
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
    }

    private void WritePollFd(ulong tableAddress, int index, int fd, short events)
    {
        Span<byte> entry = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(entry, fd);
        BinaryPrimitives.WriteInt16LittleEndian(entry[4..], events);
        BinaryPrimitives.WriteInt16LittleEndian(entry[6..], unchecked((short)0x7777)); // poisoned revents
        Assert.True(_memory.TryWrite(tableAddress + ((ulong)index * 8UL), entry));
    }

    private short ReadPollRevents(ulong tableAddress, int index)
    {
        Span<byte> revents = stackalloc byte[2];
        Assert.True(_memory.TryRead(tableAddress + ((ulong)index * 8UL) + 6, revents));
        return BinaryPrimitives.ReadInt16LittleEndian(revents);
    }

    private void WriteIoVector(ulong tableAddress, int index, ulong baseAddress, ulong length)
    {
        Span<byte> entry = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(entry, baseAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], length);
        Assert.True(_memory.TryWrite(tableAddress + ((ulong)index * 16UL), entry));
    }

    // FakeCpuMemory plus just enough IGuestAddressSpace for the mmap reservation
    // path: a bump allocator handing out ranges from the tail half of the arena.
    private sealed class FakeGuestAddressSpaceMemory : ICpuMemory, IGuestAddressSpace
    {
        private readonly ulong _base;
        private readonly byte[] _storage;
        private ulong _allocationCursor;

        public FakeGuestAddressSpaceMemory(ulong baseAddress, int size)
        {
            _base = baseAddress;
            _storage = new byte[size];
            _allocationCursor = baseAddress + ((ulong)size / 2);
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public ulong WriteCString(ulong virtualAddress, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            TryWrite(virtualAddress, bytes);
            TryWrite(virtualAddress + (ulong)bytes.Length, stackalloc byte[] { 0 });
            return virtualAddress;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            return TryAllocateAtOrAbove(0, size, executable: false, alignment, out address);
        }

        public bool TryFreeGuestMemory(ulong address) => true;

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
        {
            if (desiredAddress != 0 && TryResolve(desiredAddress, checked((int)size), out _))
            {
                return desiredAddress;
            }

            return TryAllocateAtOrAbove(desiredAddress, size, executable, 0x1000, out var address) ? address : 0;
        }

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress)
        {
            actualAddress = 0;
            var effectiveAlignment = alignment == 0 ? 0x1000UL : alignment;
            var candidate = (Math.Max(_allocationCursor, desiredAddress) + effectiveAlignment - 1) & ~(effectiveAlignment - 1);
            if (!TryResolve(candidate, checked((int)size), out _))
            {
                return false;
            }

            _allocationCursor = candidate + size;
            actualAddress = candidate;
            return true;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _base)
            {
                return false;
            }

            var relative = virtualAddress - _base;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
