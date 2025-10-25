using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Фабрика для создания обработчиков различных режимов синхронизации папок
/// Аналог folderFactory в Syncthing
/// </summary>
public class SyncFolderHandlerFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SyncFolderHandlerFactory> _logger;
    private readonly Dictionary<SyncFolderType, Func<ISyncFolderHandler>> _handlerFactories;

    public SyncFolderHandlerFactory(IServiceProvider serviceProvider, ILogger<SyncFolderHandlerFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        
        // Регистрируем фабрики для каждого типа папки
        _handlerFactories = new Dictionary<SyncFolderType, Func<ISyncFolderHandler>>
        {
            { SyncFolderType.SendReceive, () => _serviceProvider.GetRequiredService<SendReceiveFolderHandler>() },
            { SyncFolderType.SendOnly, () => _serviceProvider.GetRequiredService<SendOnlyFolderHandler>() },
            { SyncFolderType.ReceiveOnly, () => _serviceProvider.GetRequiredService<ReceiveOnlyFolderHandler>() },
            { SyncFolderType.ReceiveEncrypted, () => _serviceProvider.GetRequiredService<ReceiveEncryptedFolderHandler>() },
            { SyncFolderType.Master, () => _serviceProvider.GetRequiredService<MasterFolderHandler>() },
            { SyncFolderType.Slave, () => _serviceProvider.GetRequiredService<SlaveFolderHandler>() }
        };
    }

    /// <summary>
    /// Создать обработчик для указанного типа папки
    /// </summary>
    public ISyncFolderHandler CreateHandler(SyncFolderType folderType)
    {
        if (_handlerFactories.TryGetValue(folderType, out var factory))
        {
            var handler = factory();
            _logger.LogDebug("Created handler for folder type {FolderType}: {HandlerType}", 
                folderType, handler.GetType().Name);
            return handler;
        }

        _logger.LogWarning("Unknown folder type {FolderType}, falling back to SendReceive", folderType);
        return _handlerFactories[SyncFolderType.SendReceive]();
    }

    /// <summary>
    /// Создать обработчик для папки
    /// </summary>
    public ISyncFolderHandler CreateHandler(SyncFolder folder)
    {
        return CreateHandler(folder.SyncType);
    }

    /// <summary>
    /// Проверить, поддерживается ли указанный тип папки
    /// </summary>
    public bool IsTypeSupported(SyncFolderType folderType)
    {
        return _handlerFactories.ContainsKey(folderType);
    }

    /// <summary>
    /// Получить все поддерживаемые типы папок
    /// </summary>
    public IEnumerable<SyncFolderType> GetSupportedTypes()
    {
        return _handlerFactories.Keys;
    }

    /// <summary>
    /// Получить информацию о возможностях обработчика
    /// </summary>
    public SyncFolderCapabilities GetCapabilities(SyncFolderType folderType)
    {
        var handler = CreateHandler(folderType);
        return new SyncFolderCapabilities
        {
            FolderType = folderType,
            CanSendChanges = handler.CanSendChanges,
            CanReceiveChanges = handler.CanReceiveChanges,
            DefaultConflictPolicy = handler.GetDefaultConflictPolicy(),
            SupportedPolicies = GetSupportedPoliciesForType(folderType)
        };
    }

    /// <summary>
    /// Получить поддерживаемые политики разрешения конфликтов для типа папки
    /// </summary>
    private ConflictResolutionPolicy[] GetSupportedPoliciesForType(SyncFolderType folderType)
    {
        return folderType switch
        {
            SyncFolderType.SendReceive => new[]
            {
                ConflictResolutionPolicy.CreateCopies,
                ConflictResolutionPolicy.UseNewest,
                ConflictResolutionPolicy.UseLocal,
                ConflictResolutionPolicy.UseRemote
            },
            SyncFolderType.SendOnly => new[]
            {
                ConflictResolutionPolicy.UseLocal,
                ConflictResolutionPolicy.Override
            },
            SyncFolderType.ReceiveOnly => new[]
            {
                ConflictResolutionPolicy.UseRemote,
                ConflictResolutionPolicy.Revert
            },
            SyncFolderType.ReceiveEncrypted => new[]
            {
                ConflictResolutionPolicy.UseRemote,
                ConflictResolutionPolicy.Revert
            },
            SyncFolderType.Master => new[]
            {
                ConflictResolutionPolicy.Override,
                ConflictResolutionPolicy.UseLocal
            },
            SyncFolderType.Slave => new[]
            {
                ConflictResolutionPolicy.Revert,
                ConflictResolutionPolicy.UseRemote
            },
            _ => new[] { ConflictResolutionPolicy.CreateCopies }
        };
    }
}

/// <summary>
/// Информация о возможностях обработчика папки
/// </summary>
public class SyncFolderCapabilities
{
    /// <summary>
    /// Тип папки
    /// </summary>
    public SyncFolderType FolderType { get; set; }
    
    /// <summary>
    /// Может ли отправлять изменения
    /// </summary>
    public bool CanSendChanges { get; set; }
    
    /// <summary>
    /// Может ли получать изменения
    /// </summary>
    public bool CanReceiveChanges { get; set; }
    
    /// <summary>
    /// Политика разрешения конфликтов по умолчанию
    /// </summary>
    public ConflictResolutionPolicy DefaultConflictPolicy { get; set; }
    
    /// <summary>
    /// Поддерживаемые политики разрешения конфликтов
    /// </summary>
    public ConflictResolutionPolicy[] SupportedPolicies { get; set; } = Array.Empty<ConflictResolutionPolicy>();
    
    /// <summary>
    /// Описание режима синхронизации
    /// </summary>
    public string Description => FolderType switch
    {
        SyncFolderType.SendReceive => "Полная двусторонняя синхронизация с разрешением конфликтов",
        SyncFolderType.SendOnly => "Только отправка локальных изменений, удаленные игнорируются",
        SyncFolderType.ReceiveOnly => "Только получение удаленных изменений, локальные помечаются",
        SyncFolderType.ReceiveEncrypted => "Получение зашифрованных данных без расшифровки",
        SyncFolderType.Master => "Источник истины, принудительно перезаписывает удаленные изменения",
        SyncFolderType.Slave => "Ведомый режим, автоматически откатывает локальные изменения",
        _ => "Неизвестный режим синхронизации"
    };
}