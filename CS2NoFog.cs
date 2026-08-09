using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2NoFog;

[MinimumApiVersion(200)]
public class CS2NoFogPlugin : BasePlugin
{
    public override string ModuleName => "CS2-NoFog";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Removes fog on any map when an admin types !nofog in chat.";

    private static readonly string ChatPrefix = $" {ChatColors.Green}[NoFog]{ChatColors.Default}";

    private bool _noFogEnabled;

    private readonly Dictionary<uint, FogControllerState> _savedFogControllers = new();
    private readonly Dictionary<uint, bool> _savedGradientFog = new();
    private readonly Dictionary<uint, bool> _savedCubemapFog = new();

    private sealed record FogControllerState(bool Enable, float Start, float End, float MaxDensity);

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
    }

    [ConsoleCommand("css_nofog", "Toggles fog removal on the current map.")]
    [RequiresPermissions("@css/generic")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnNoFogCommand(CCSPlayerController? player, CommandInfo command)
    {
        _noFogEnabled = !_noFogEnabled;

        if (_noFogEnabled)
        {
            RemoveFog();
            command.ReplyToCommand($"{ChatPrefix} Fog has been removed on this map.");
        }
        else
        {
            RestoreFog();
            command.ReplyToCommand($"{ChatPrefix} Fog has been restored to map defaults.");
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Round restarts reset entity state, so re-apply once entities have settled.
        if (_noFogEnabled)
        {
            Server.NextFrame(RemoveFog);
        }

        return HookResult.Continue;
    }

    private void OnMapEnd()
    {
        _noFogEnabled = false;
        ClearSavedState();
    }

    private void RemoveFog()
    {
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CFogController>("env_fog_controller"))
        {
            if (!fog.IsValid)
                continue;

            if (!_savedFogControllers.ContainsKey(fog.Index))
            {
                _savedFogControllers[fog.Index] =
                    new FogControllerState(fog.Fog.Enable, fog.Fog.Start, fog.Fog.End, fog.Fog.Maxdensity);
            }

            fog.Fog.Enable = false;
            fog.Fog.Start = 100000f;
            fog.Fog.End = 200000f;
            fog.Fog.Maxdensity = 0f;
            Utilities.SetStateChanged(fog, "CFogController", "m_fog");
        }

        foreach (var gradient in Utilities.FindAllEntitiesByDesignerName<CGradientFog>("env_gradient_fog"))
        {
            if (!gradient.IsValid)
                continue;

            if (!_savedGradientFog.ContainsKey(gradient.Index))
                _savedGradientFog[gradient.Index] = gradient.IsEnabled;

            gradient.IsEnabled = false;
            Utilities.SetStateChanged(gradient, "CGradientFog", "m_bIsEnabled");
        }

        foreach (var cubemap in Utilities.FindAllEntitiesByDesignerName<CEnvCubemapFog>("env_cubemap_fog"))
        {
            if (!cubemap.IsValid)
                continue;

            if (!_savedCubemapFog.ContainsKey(cubemap.Index))
                _savedCubemapFog[cubemap.Index] = cubemap.Active;

            cubemap.Active = false;
            Utilities.SetStateChanged(cubemap, "CEnvCubemapFog", "m_bActive");
        }
    }

    private void RestoreFog()
    {
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CFogController>("env_fog_controller"))
        {
            if (!fog.IsValid || !_savedFogControllers.TryGetValue(fog.Index, out var saved))
                continue;

            fog.Fog.Enable = saved.Enable;
            fog.Fog.Start = saved.Start;
            fog.Fog.End = saved.End;
            fog.Fog.Maxdensity = saved.MaxDensity;
            Utilities.SetStateChanged(fog, "CFogController", "m_fog");
        }

        foreach (var gradient in Utilities.FindAllEntitiesByDesignerName<CGradientFog>("env_gradient_fog"))
        {
            if (!gradient.IsValid || !_savedGradientFog.TryGetValue(gradient.Index, out var enabled))
                continue;

            gradient.IsEnabled = enabled;
            Utilities.SetStateChanged(gradient, "CGradientFog", "m_bIsEnabled");
        }

        foreach (var cubemap in Utilities.FindAllEntitiesByDesignerName<CEnvCubemapFog>("env_cubemap_fog"))
        {
            if (!cubemap.IsValid || !_savedCubemapFog.TryGetValue(cubemap.Index, out var active))
                continue;

            cubemap.Active = active;
            Utilities.SetStateChanged(cubemap, "CEnvCubemapFog", "m_bActive");
        }

        ClearSavedState();
    }

    private void ClearSavedState()
    {
        _savedFogControllers.Clear();
        _savedGradientFog.Clear();
        _savedCubemapFog.Clear();
    }
}
