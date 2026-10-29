using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nimbo;

public sealed class MemoryCleaner
{
    public CleanupRecord Clean(
        MemorySnapshot before,
        bool trimWorkingSets,
        bool trimSystemFileCache,
        bool cleanUserTempFiles,
        CancellationToken cancellationToken = default)
    {
        if (!trimWorkingSets && !trimSystemFileCache && !cleanUserTempFiles)
            throw new InvalidOperationException("Не выбрано ни одной области очистки.");

        var record = new CleanupRecord
        {
            StartedAt = DateTimeOffset.Now,
            AvailableBefore = before.PhysicalAvailable
        };
        var errors = new List<string>();
        var successfulAreas = 0;
        var selectedAreas = 0;

        if (trimWorkingSets)
        {
            selectedAreas++;
            record.SelectedAreas.Add("Рабочие наборы приложений");
            var (trimmed, skipped) = TrimUserWorkingSets(cancellationToken);
            record.ProcessesTrimmed = trimmed;
            record.ProcessesSkipped = skipped;
            if (trimmed > 0)
                successfulAreas++;
            else
                errors.Add("Не удалось изменить рабочие наборы процессов.");
            if (skipped > 0 && trimmed > 0)
                errors.Add($"Пропущено процессов: {skipped}.");
        }

        if (trimSystemFileCache)
        {
            selectedAreas++;
            record.SelectedAreas.Add("Файловый кэш Windows");
            try
            {
                ClearSystemFileCacheWorkingSet();
                record.SystemFileCacheTrimmed = true;
                successfulAreas++;
            }
            catch (Exception ex)
            {
                errors.Add($"Файловый кэш: {ex.Message}");
            }
        }

        if (cleanUserTempFiles)
        {
            selectedAreas++;
            record.SelectedAreas.Add("Временные файлы пользователя");
            try
            {
                var result = CleanUserTempFiles(cancellationToken);
                record.TempFilesDeleted = result.Deleted;
                record.TempFilesSkipped = result.Skipped;
                record.TempBytesDeleted = result.BytesDeleted;
                if (result.Deleted > 0 || result.Found == 0)
                    successfulAreas++;
                if (result.Skipped > 0)
                    errors.Add($"Занятых или недоступных временных файлов пропущено: {result.Skipped}.");
                if (result.Found > 0 && result.Deleted == 0 && result.Skipped == 0)
                    errors.Add("Не удалось удалить временные файлы.");
            }
            catch (Exception ex)
            {
                errors.Add($"Временные файлы: {ex.Message}");
            }
        }

        record.FinishedAt = DateTimeOffset.Now;
        record.Status = successfulAreas == selectedAreas
            ? errors.Count == 0 ? "completed" : "partial"
            : successfulAreas > 0 ? "partial" : "failed";
        var details = new List<string>();
        if (record.TempFilesDeleted > 0)
            details.Add($"Удалено временных файлов: {record.TempFilesDeleted} ({FormatBytes(record.TempBytesDeleted)}).");
        if (record.SystemFileCacheTrimmed || record.ProcessesTrimmed > 0)
            details.Add("Windows может повторно использовать освобождённые страницы.");
        details.AddRange(errors);
        record.Note = details.Count == 0
            ? "В выбранных областях не было файлов для удаления."
            : string.Join(" ", details);
        return record;
    }

    private static TempCleanupResult CleanUserTempFiles(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!IsChildPath(root, profile) && !IsChildPath(root, localAppData))
            throw new InvalidOperationException("Папка TEMP находится вне профиля пользователя, поэтому её содержимое не трогали.");
        if (!Directory.Exists(root))
            return new TempCleanupResult(0, 0, 0, 0);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Папка TEMP является ссылкой; её содержимое не трогали.");

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        var found = 0;
        var deleted = 0;
        var skipped = 0;
        ulong bytesDeleted = 0;

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch
            {
                skipped++;
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch { skipped++; continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                    continue;
                }

                found++;
                try
                {
                    var length = (ulong)new FileInfo(entry).Length;
                    File.Delete(entry);
                    deleted++;
                    bytesDeleted += length;
                }
                catch
                {
                    skipped++;
                }
            }
        }

        return new TempCleanupResult(found, deleted, skipped, bytesDeleted);
    }

    private static bool IsChildPath(string path, string parent)
    {
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return index == 0 ? $"{value:0} {units[index]}" : $"{value:0.0} {units[index]}";
    }

    private sealed record TempCleanupResult(int Found, int Deleted, int Skipped, ulong BytesDeleted);

    private static (int Trimmed, int Skipped) TrimUserWorkingSets(CancellationToken cancellationToken)
    {
        var trimmed = 0;
        var skipped = 0;
        var currentSession = Process.GetCurrentProcess().SessionId;
        var currentPid = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (process)
            {
                try
                {
                    if (process.Id == currentPid || process.SessionId != currentSession || process.Id < 100)
                        continue;

                    var handle = OpenProcess(SetQuota | QueryInformation, false, process.Id);
                    if (handle == IntPtr.Zero)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        if (EmptyWorkingSet(handle))
                            trimmed++;
                        else
                            skipped++;
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
                catch
                {
                    skipped++;
                }
            }
        }
        return (trimmed, skipped);
    }

    private static void ClearSystemFileCacheWorkingSet()
    {
        var token = IntPtr.Zero;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
            throw new InvalidOperationException("Windows не выдал доступ к привилегиям процесса.");

        TokenPrivileges previous = default;
        var changed = false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeIncreaseQuotaPrivilege", out var luid))
                throw new InvalidOperationException("Не удалось найти системную привилегию для кэша.");

            var enabled = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };
            if (!AdjustTokenPrivileges(token, false, ref enabled,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), out previous, out _))
                throw new InvalidOperationException("Windows отказал в настройке привилегии для кэша.");

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotAllAssigned)
                throw new UnauthorizedAccessException("Для очистки системного кэша нужны права администратора.");
            changed = previous.PrivilegeCount > 0;

            var allPages = UIntPtr.Size == 8
                ? new UIntPtr(ulong.MaxValue)
                : new UIntPtr(uint.MaxValue);
            if (!SetSystemFileCacheSize(allPages, allPages, 0))
                throw new InvalidOperationException("Windows не смог изменить рабочий набор файлового кэша.");
        }
        finally
        {
            if (changed)
                AdjustTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
            CloseHandle(token);
        }
    }

    private const uint SetQuota = 0x0100;
    private const uint QueryInformation = 0x0400;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, out TokenPrivileges previousState, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemFileCacheSize(UIntPtr minimumFileCacheSize, UIntPtr maximumFileCacheSize, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
