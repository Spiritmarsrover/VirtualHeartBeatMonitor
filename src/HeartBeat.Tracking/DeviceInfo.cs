using System.Text;
using Valve.VR;

namespace HeartBeat.Tracking;

public sealed record DeviceInfo(
    uint Index,
    ETrackedDeviceClass Class,
    ETrackedControllerRole Role,
    string TrackingSystem,
    string Model,
    string Serial,
    string Manufacturer,
    string ControllerType)
{
    /// <summary>Short human-readable kind, e.g. "HMD", "Controller-L", "Tracker".</summary>
    public string Kind => Class switch
    {
        ETrackedDeviceClass.HMD => "HMD",
        ETrackedDeviceClass.Controller => Role switch
        {
            ETrackedControllerRole.LeftHand => "Controller-L",
            ETrackedControllerRole.RightHand => "Controller-R",
            _ => "Controller",
        },
        ETrackedDeviceClass.GenericTracker => "Tracker",
        ETrackedDeviceClass.TrackingReference => "BaseStation",
        ETrackedDeviceClass.DisplayRedirect => "DisplayRedirect",
        _ => Class.ToString(),
    };

    /// <summary>Devices that move with the user (excludes base stations / tracking references).</summary>
    public bool IsBodyDevice => Class is ETrackedDeviceClass.HMD
        or ETrackedDeviceClass.Controller
        or ETrackedDeviceClass.GenericTracker;

    public string Label => $"#{Index} {Kind} {Model} [{Serial}]";

    public static IReadOnlyList<DeviceInfo> Enumerate(CVRSystem system)
    {
        var list = new List<DeviceInfo>();
        for (uint i = 0; i < Valve.VR.OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            var cls = system.GetTrackedDeviceClass(i);
            if (cls == ETrackedDeviceClass.Invalid || !system.IsTrackedDeviceConnected(i))
                continue;

            list.Add(new DeviceInfo(
                i,
                cls,
                system.GetControllerRoleForTrackedDeviceIndex(i),
                GetString(system, i, ETrackedDeviceProperty.Prop_TrackingSystemName_String),
                GetString(system, i, ETrackedDeviceProperty.Prop_ModelNumber_String),
                GetString(system, i, ETrackedDeviceProperty.Prop_SerialNumber_String),
                GetString(system, i, ETrackedDeviceProperty.Prop_ManufacturerName_String),
                GetString(system, i, ETrackedDeviceProperty.Prop_ControllerType_String)));
        }
        return list;
    }

    private static string GetString(CVRSystem system, uint index, ETrackedDeviceProperty prop)
    {
        var error = ETrackedPropertyError.TrackedProp_Success;
        uint length = system.GetStringTrackedDeviceProperty(index, prop, null, 0, ref error);
        if (length <= 1) return "";
        var sb = new StringBuilder((int)length);
        system.GetStringTrackedDeviceProperty(index, prop, sb, length, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success ? sb.ToString() : "";
    }
}
