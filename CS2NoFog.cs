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
    public override string ModuleVersion => "1.3.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Removes fog on any map when an admin types !nofog in chat.";

    private const float FarPlane = 9999999f;

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
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
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

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        // Late joiners (and respawns) get a fresh pawn whose fog params point at
        // whatever controller the engine picked; rebind them to ours.
        if (_noFogEnabled)
        {
            Server.NextFrame(() =>
            {
                var replacement = FindReplacementController();
                if (replacement != null)
                {
                    foreach (var pawn in AlivePlayerPawns())
                        BindPlayerFog(pawn, replacement);
                }
            });
        }

        return HookResult.Continue;
    }

    private void OnMapEnd()
    {
        _noFogEnabled = false;
    }

    private void RemoveFog()
    {
        // Deleting the fog entities is what actually clears fog on the client -
        // disabling them in place does not reliably replicate. But the map's
        // env_fog_controller also owns the far-Z clip plane (draw distance), and
        // deleting it makes the engine fall back to a close default plane where
        // everything renders black. So: delete the map's fog entities, then spawn
        // our own fog-disabled env_fog_controller with a huge far-Z and bind every
        // player's fog params to it.
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

        Server.NextFrame(() =>
        {
            var replacement = FindReplacementController() ?? SpawnReplacementController();
            if (replacement == null)
                return;

            foreach (var pawn in AlivePlayerPawns())
                BindPlayerFog(pawn, replacement);
        });
    }

    private static CFogController? FindReplacementController()
    {
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CFogController>("env_fog_controller"))
        {
            if (fog.IsValid && fog.Entity?.Name == "nofog_controller")
                return fog;
        }

        return null;
    }

    private static CFogController? SpawnReplacementController()
    {
        var fog = Utilities.CreateEntityByName<CFogController>("env_fog_controller");
        if (fog == null || !fog.IsValid)
            return null;

        fog.Entity!.Name = "nofog_controller";
        ConfigureNoFogParams(fog);
        fog.DispatchSpawn();
        ConfigureNoFogParams(fog);
        Utilities.SetStateChanged(fog, "CFogController", "m_fog");
        return fog;
    }

    private static void ConfigureNoFogParams(CFogController fog)
    {
        fog.Fog.Enable = false;
        fog.Fog.Start = FarPlane;
        fog.Fog.End = FarPlane;
        fog.Fog.Maxdensity = 0f;
        fog.Fog.Farz = FarPlane;
    }

    private static IEnumerable<CBasePlayerPawn> AlivePlayerPawns()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsValid && !player.IsHLTV && player.Pawn.Value is { IsValid: true } pawn)
                yield return pawn;
        }
    }

    private static void BindPlayerFog(CBasePlayerPawn pawn, CFogController fog)
    {
        var camera = pawn.CameraServices;
        if (camera == null)
            return;

        var playerFog = camera.PlayerFog;
        playerFog.Ctrl.Raw = fog.EntityHandle.Raw;
        // Force both ends of the fog lerp to "no fog, huge far plane" so nothing
        // lingers from the deleted map controller.
        playerFog.OldFarZ = FarPlane;
        playerFog.NewFarZ = FarPlane;
        playerFog.OldMaxDensity = 0f;
        playerFog.NewMaxDensity = 0f;
        playerFog.OldStart = FarPlane;
        playerFog.NewStart = FarPlane;
        playerFog.OldEnd = FarPlane;
        playerFog.NewEnd = FarPlane;
        Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pCameraServices");
    }
}
