// Stub types for NatTraversalTests - these are placeholders for types not yet implemented
// TODO: Remove this file once the actual types are implemented in the Infrastructure project

using System.Net;

namespace CreatioHelper.Infrastructure.Services.Network.UPnP
{
    /// <summary>
    /// Stub for UPnPServiceStatus - to be implemented in Infrastructure project
    /// </summary>
    public class UPnPServiceStatus
    {
        public bool IsAvailable { get; set; }
        public int DeviceCount { get; set; }
        public int Igdv1DeviceCount { get; set; }
        public int Igdv2DeviceCount { get; set; }
        public int Ipv6DeviceCount { get; set; }
        public int ActiveMappingCount { get; set; }
        public int ActivePinholeCount { get; set; }
        public string? ExternalIPv4 { get; set; }
        public string? ExternalIPv6 { get; set; }
    }

    /// <summary>
    /// Stub for UPnPAnyPortMappingResult - to be implemented in Infrastructure project
    /// </summary>
    public class UPnPAnyPortMappingResult
    {
        public int AssignedExternalPort { get; set; }
        public int InternalPort { get; set; }
        public string Protocol { get; set; } = "";
        public int LeaseDuration { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    /// <summary>
    /// Stub for UPnPErrorCodes - to be implemented in Infrastructure project
    /// </summary>
    public static class UPnPErrorCodes
    {
        public const int InvalidArgs = 402;
        public const int ActionFailed = 501;
        public const int NotAuthorized = 606;
        public const int NoSuchEntryInArray = 714;
        public const int ConflictInMappingEntry = 718;
        public const int OnlyPermanentLeasesSupported = 725;
        public const int NoPortMapsAvailable = 728;
    }
}

namespace CreatioHelper.Infrastructure.Services.Network.Stun
{
    /// <summary>
    /// Stub for StunServiceStatus - to be implemented in Infrastructure project
    /// </summary>
    public class StunServiceStatus
    {
        public bool IsRunning { get; set; }
        public string? MappedAddress { get; set; }
        public int? MappedPort { get; set; }
        public NatType NatType { get; set; }
        public string NatTypeDescription { get; set; } = "";
        public bool IsPunchable { get; set; }
        public IPEndPoint? ExternalEndpoint { get; set; }
        public int SuccessfulChecks { get; set; }
        public int FailedChecks { get; set; }
        public List<string> ConfiguredServers { get; set; } = new();
        public DateTime? LastCheck { get; set; }
    }
}
