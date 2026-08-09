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
    public override string ModuleVersion => "1.4.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Removes fog on any map when an admin types !nofog in chat.";

    private const float FarPlane = 9999999f;

    private static readonly string ChatPrefix = $" {ChatColors.Green}[NoFog]{ChatColors.Default}";

    // Deleted outright; this reliably clears their fog on clients. env_fog_controller
    // is intentionally NOT in this list - see RemoveFog.
    private static readonly string[] DeletedFogEntityNames =
    {
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
        // Fresh pawns get fog params from the map controller's original state;
        // re-push the no-fog params to everyone.
        if (_noFogEnabled)
        {
            Server.NextFrame(ApplyPlayerFogParams);
        }

        return HookResult.Continue;
    }

    private void OnMapEnd()
    {
        _noFogEnabled = false;
    }

    private void RemoveFog()
    {
        foreach (var designerName in DeletedFogEntityNames)
        {
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(designerName))
            {
                if (entity.IsValid)
                    entity.Remove();
            }
        }

        // env_fog_controller must stay alive: it owns the far-Z clip plane, and the
        // engine only honors far-Z while fog is ENABLED (with it disabled or the
        // entity deleted, the client clips at a close default plane and renders
        // black beyond it). So keep fog turned ON but invisible - density zero,
        // start/end pushed out - and push far-Z out to ~10M units. Driving this
        // through entity inputs makes the game propagate the change to clients the
        // same way map I/O would, instead of relying on raw field replication.
        var controller = FindOrCreateFogController();
        if (controller != null)
        {
            controller.AcceptInput("TurnOn");
            controller.AcceptInput("SetMaxDensity", null, null, "0");
            controller.AcceptInput("SetStartDist", null, null, FarPlane.ToString("F0"));
            controller.AcceptInput("SetEndDist", null, null, FarPlane.ToString("F0"));
            controller.AcceptInput("SetFarZ", null, null, FarPlane.ToString("F0"));

            // Belt and braces: mirror the same values onto the networked struct.
            controller.Fog.Enable = true;
            controller.Fog.Start = FarPlane;
            controller.Fog.End = FarPlane;
            controller.Fog.Maxdensity = 0f;
            controller.Fog.Farz = FarPlane;
            Utilities.SetStateChanged(controller, "CFogController", "m_fog");
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

        Server.NextFrame(ApplyPlayerFogParams);
    }

    private static CFogController? FindOrCreateFogController()
    {
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CFogController>("env_fog_controller"))
        {
            if (fog.IsValid)
                return fog;
        }

        var spawned = Utilities.CreateEntityByName<CFogController>("env_fog_controller");
        if (spawned == null || !spawned.IsValid)
            return null;

        spawned.DispatchSpawn();
        return spawned;
    }

    private static void ApplyPlayerFogParams()
    {
        var controller = default(CFogController);
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CFogController>("env_fog_controller"))
        {
            if (fog.IsValid)
            {
                controller = fog;
                break;
            }
        }

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsHLTV || player.Pawn.Value is not { IsValid: true } pawn)
                continue;

            var camera = pawn.CameraServices;
            if (camera == null)
                continue;

            // Force both ends of the client's fog lerp to "invisible fog, huge
            // far plane" so nothing lingers from the map's original settings.
            var playerFog = camera.PlayerFog;
            if (controller != null)
                playerFog.Ctrl.Raw = controller.EntityHandle.Raw;
            playerFog.TransitionTime = 0f;
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
}
