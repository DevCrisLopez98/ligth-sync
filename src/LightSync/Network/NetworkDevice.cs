namespace LightSync.Network;

public enum NetworkKind
{
    Light,
    Plug,
    Other,
}

public sealed class NetworkDevice
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Host { get; init; }

    public required string Transport { get; init; }

    public required NetworkKind Kind { get; init; }

    public string? Model { get; init; }

    public bool? IsOn { get; init; }

    public string? Mode { get; init; }

    public string? Meter { get; init; }

    public string Role => Kind switch
    {
        NetworkKind.Light => "Foco Wi-Fi",
        NetworkKind.Plug => "Enchufe",
        _ => "Red",
    };

    public string State => IsOn switch
    {
        true => Kind == NetworkKind.Light ? "Encendida" : "Encendido",
        false => Kind == NetworkKind.Light ? "Apagada" : "Apagado",
        _ => "Sin estado",
    };

    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Model))
            {
                parts.Add(Model);
            }

            parts.Add(Host);
            parts.Add(Transport);
            if (!string.IsNullOrWhiteSpace(Mode))
            {
                parts.Add(Mode);
            }

            if (!string.IsNullOrWhiteSpace(Meter))
            {
                parts.Add(Meter);
            }

            return string.Join(" · ", parts);
        }
    }
}
