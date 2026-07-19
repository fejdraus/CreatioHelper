using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Движок разрешения конфликтов, основанный на алгоритмах Syncthing
/// </summary>
public class ConflictResolutionEngine
{
    private readonly ILogger<ConflictResolutionEngine> _logger;
    
    public ConflictResolutionEngine(ILogger<ConflictResolutionEngine> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Разрешить конфликт между локальным и удаленным файлом
    /// </summary>
    public ConflictResolution ResolveConflict(FileMetadata local, FileMetadata remote, 
        SyncFolderType folderType, ConflictResolutionPolicy? customPolicy = null)
    {
        var policy = customPolicy ?? GetDefaultPolicyForFolderType(folderType);
        
        _logger.LogDebug("Resolving conflict for {FileName} using policy {Policy} in folder type {FolderType}",
            local.FileName, policy, folderType);
        
        return policy switch
        {
            ConflictResolutionPolicy.CreateCopies => CreateConflictCopyResolution(local, remote),
            ConflictResolutionPolicy.UseNewest => UseNewestResolution(local, remote),
            ConflictResolutionPolicy.UseLocal => new ConflictResolution { Action = ConflictAction.UseLocal, Winner = local },
            ConflictResolutionPolicy.UseRemote => new ConflictResolution { Action = ConflictAction.UseRemote, Winner = remote },
            ConflictResolutionPolicy.Override => new ConflictResolution { Action = ConflictAction.Override, Winner = local },
            ConflictResolutionPolicy.Revert => new ConflictResolution { Action = ConflictAction.Revert, Winner = remote },
            _ => throw new ArgumentException($"Unknown conflict resolution policy: {policy}")
        };
    }
    
    /// <summary>
    /// Разрешить конфликт на основе типа папки (аналог Syncthing folder-specific logic)
    /// </summary>
    public ConflictResolution ResolveConflictByFolderType(FileMetadata local, FileMetadata remote, SyncFolderType folderType)
    {
        return folderType switch
        {
            SyncFolderType.SendReceive => ResolveBidirectionalConflict(local, remote),
            SyncFolderType.SendOnly => new ConflictResolution { Action = ConflictAction.UseLocal, Winner = local, Reason = "Send-only folder prefers local" },
            SyncFolderType.ReceiveOnly => new ConflictResolution { Action = ConflictAction.UseRemote, Winner = remote, Reason = "Receive-only folder prefers remote" },
            SyncFolderType.Master => new ConflictResolution { Action = ConflictAction.Override, Winner = local, Reason = "Master folder overrides remote" },
            SyncFolderType.Slave => new ConflictResolution { Action = ConflictAction.Revert, Winner = remote, Reason = "Slave folder reverts to remote" },
            _ => throw new ArgumentException($"Unknown folder type: {folderType}")
        };
    }
    
    /// <summary>
    /// Проверить, есть ли конфликт между файлами (аналог Syncthing inConflict)
    /// </summary>
    public bool IsInConflict(FileMetadata local, FileMetadata remote)
    {
        // Если файлы имеют разные векторные часы и они конкурентны
        if (local.VersionVector.IsConcurrentWith(remote.VersionVector))
        {
            return true;
        }
        
        // Если времена модификации одинаковы, но содержимое разное
        if (local.ModifiedTime == remote.ModifiedTime && local.Hash != remote.Hash)
        {
            return true;
        }
        
        // Если один файл удален, а другой изменен
        if (local.IsDeleted != remote.IsDeleted)
        {
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Определить тип конфликта
    /// </summary>
    public ConflictType DetermineConflictType(FileMetadata local, FileMetadata remote)
    {
        if (local.IsDeleted && !remote.IsDeleted)
            return ConflictType.DeletedLocallyModifiedRemotely;
        
        if (!local.IsDeleted && remote.IsDeleted)
            return ConflictType.ModifiedLocallyDeletedRemotely;
        
        if (local.Permissions != remote.Permissions)
            return ConflictType.PermissionConflict;
        
        if (local.LocalFlags.HasFlag(FileLocalFlags.ReceiveOnly))
            return ConflictType.UnexpectedLocalChange;
        
        return ConflictType.ConcurrentModification;
    }
    
    /// <summary>
    /// Получить политику по умолчанию для типа папки
    /// </summary>
    public static ConflictResolutionPolicy GetDefaultPolicyForFolderType(SyncFolderType folderType)
    {
        return folderType switch
        {
            SyncFolderType.SendReceive => ConflictResolutionPolicy.CreateCopies,
            SyncFolderType.SendOnly => ConflictResolutionPolicy.UseLocal,
            SyncFolderType.ReceiveOnly => ConflictResolutionPolicy.UseRemote,
            SyncFolderType.Master => ConflictResolutionPolicy.Override,
            SyncFolderType.Slave => ConflictResolutionPolicy.Revert,
            _ => ConflictResolutionPolicy.CreateCopies
        };
    }
    
    /// <summary>
    /// Проверить победителя конфликта (аналог Syncthing WinsConflict)
    /// </summary>
    public FileMetadata DetermineWinner(FileMetadata local, FileMetadata remote)
    {
        // 1. Невалидные файлы всегда проигрывают
        if (local.LocalFlags.HasFlag(FileLocalFlags.Invalid) != remote.LocalFlags.HasFlag(FileLocalFlags.Invalid))
        {
            return local.LocalFlags.HasFlag(FileLocalFlags.Invalid) ? remote : local;
        }
        
        // 2. Более новое время модификации выигрывает
        if (local.ModifiedTime > remote.ModifiedTime)
            return local;
        if (local.ModifiedTime < remote.ModifiedTime)
            return remote;
        
        // 3. При равном времени - используется размер файла
        if (local.Size != remote.Size)
            return local.Size > remote.Size ? local : remote;
        
        // 4. В качестве последнего критерия используется hash
        var localHashString = local.Hash != null ? System.Text.Encoding.UTF8.GetString(local.Hash) : string.Empty;
        var remoteHashString = remote.Hash != null ? System.Text.Encoding.UTF8.GetString(remote.Hash) : string.Empty;
        var hashComparison = string.Compare(localHashString, remoteHashString, StringComparison.Ordinal);
        return hashComparison > 0 ? local : remote;
    }
    
    /// <summary>
    /// Создать имя конфликтного файла (аналог Syncthing conflictName)
    /// </summary>
    public string CreateConflictFileName(string originalName, string deviceId, DateTime conflictTime)
    {
        var timestamp = conflictTime.ToString("yyyyMMdd-HHmmss");
        var extension = Path.GetExtension(originalName);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(originalName);
        var directory = Path.GetDirectoryName(originalName) ?? "";
        
        var conflictName = $"{nameWithoutExt}.sync-conflict-{timestamp}-{deviceId}{extension}";
        return Path.Combine(directory, conflictName);
    }
    
    private ConflictResolution CreateConflictCopyResolution(FileMetadata local, FileMetadata remote)
    {
        var winner = DetermineWinner(local, remote);
        var loser = winner == local ? remote : local;
        
        var conflictFileName = CreateConflictFileName(
            loser.FileName, 
            loser.DeviceId ?? "unknown", 
            DateTime.UtcNow);
        
        return new ConflictResolution
        {
            Action = ConflictAction.CreateConflictCopy,
            Winner = winner,
            ConflictFileName = conflictFileName,
            Reason = $"Concurrent modification, creating conflict copy"
        };
    }
    
    private ConflictResolution UseNewestResolution(FileMetadata local, FileMetadata remote)
    {
        var winner = local.ModifiedTime >= remote.ModifiedTime ? local : remote;
        return new ConflictResolution
        {
            Action = winner == local ? ConflictAction.UseLocal : ConflictAction.UseRemote,
            Winner = winner,
            Reason = $"Using newest version (modified {winner.ModifiedTime})"
        };
    }
    
    private ConflictResolution ResolveBidirectionalConflict(FileMetadata local, FileMetadata remote)
    {
        // Causal ordering decides first: a strictly-newer version wins cleanly, with no
        // conflict copy. Only genuinely concurrent edits are a real conflict. This matches
        // Syncthing's vector-clock semantics and keeps this path consistent with the
        // detection in IsInConflict (both use the vector clock, not mtime/hash).
        switch (local.VersionVector.Compare(remote.VersionVector))
        {
            case VectorClockComparison.Greater:
                return new ConflictResolution
                {
                    Action = ConflictAction.UseLocal,
                    Winner = local,
                    Reason = "Local version is causally newer (vector clock)"
                };

            case VectorClockComparison.Lesser:
                return new ConflictResolution
                {
                    Action = ConflictAction.UseRemote,
                    Winner = remote,
                    Reason = "Remote version is causally newer (vector clock)"
                };

            case VectorClockComparison.Concurrent:
                return CreateConflictCopyResolution(local, remote);

            case VectorClockComparison.Equal:
            default:
                // Same version vector: if content is identical there is nothing to do,
                // otherwise fall back to a deterministic tie-break.
                if (ContentEquals(local, remote))
                {
                    return new ConflictResolution
                    {
                        Action = ConflictAction.Skip,
                        Winner = local,
                        Reason = "Files are identical"
                    };
                }

                var winner = DetermineWinner(local, remote);
                return new ConflictResolution
                {
                    Action = winner == local ? ConflictAction.UseLocal : ConflictAction.UseRemote,
                    Winner = winner,
                    Reason = "Equal version vectors; resolved by deterministic tie-break"
                };
        }
    }

    private static bool ContentEquals(FileMetadata local, FileMetadata remote)
    {
        if (local.IsDeleted || remote.IsDeleted)
        {
            return local.IsDeleted == remote.IsDeleted;
        }
        if (local.Size != remote.Size)
        {
            return false;
        }
        var lh = local.Hash ?? Array.Empty<byte>();
        var rh = remote.Hash ?? Array.Empty<byte>();
        return lh.AsSpan().SequenceEqual(rh);
    }
    
}