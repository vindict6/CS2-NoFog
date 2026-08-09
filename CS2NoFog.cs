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
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Removes fog on any map when an admin types !nofog in chat.";

    private static readonly string ChatPrefix = $" {ChatColors.Green}[NoFog]{ChatColors.Default}";

    private static readonly string[] FogEntityNames =
    {
        "env_fog_controller",
        "env_gradient_fog",
        "env_cubemap_fog",
        "env_player_visibility",
    };

    private bool _noFogEnabled;

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
            command.ReplyToCommand($"{ChatPrefix} Fog entities have been removed on this map.");
        }
        else
        {
            command.ReplyToCommand($"{ChatPrefix} Fog removal disabled. Fog returns next round restart.");
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Round restarts respawn map entities, so re-apply once entities have settled.
        if (_noFogEnabled)
        {
            Server.NextFrame(RemoveFog);
        }

        return HookResult.Continue;
    }

    private void OnMapEnd()
    {
        _noFogEnabled = false;
    }

    private void RemoveFog()
    {
        foreach (var designerName in FogEntityNames)
        {
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(designerName))
            {
                if (entity.IsValid)
                    entity.Remove();
            }
        }

        // The 3D skybox fog lives on sky_camera; killing that entity would remove
        // the entire skybox, so zero out its fog parameters instead.
        foreach (var sky in Utilities.FindAllEntitiesByDesignerName<CSkyCamera>("sky_camera"))
        {
            if (!sky.IsValid)
                continue;

            var fog = sky.SkyboxData.Fog;
            fog.Enable = false;
            fog.Maxdensity = 0f;
            fog.Start = 100000f;
            fog.End = 200000f;
            Utilities.SetStateChanged(sky, "CSkyCamera", "m_skyboxData");
        }
    }
}
