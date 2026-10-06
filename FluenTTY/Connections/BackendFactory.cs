using FluenTTY.Models;

namespace FluenTTY.Connections;

public static class BackendFactory
{
    public static ITerminalBackend Create(SessionProfile profile, AppSettings settings) => profile.Protocol switch
    {
        ProtocolKind.Ssh => new SshBackend(profile, settings),
        ProtocolKind.Telnet => new TelnetBackend(profile, settings),
        ProtocolKind.Rlogin => new RloginBackend(profile, settings),
        ProtocolKind.Rsh => new RshBackend(profile, settings),
        ProtocolKind.Raw => new RawBackend(profile, settings),
        ProtocolKind.Serial => new SerialBackend(profile, settings),
        _ => throw new NotSupportedException($"{profile.Protocol} is not a terminal protocol"),
    };
}
