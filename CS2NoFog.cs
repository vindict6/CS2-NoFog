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
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Removes fog on any map when an admin types !nofog in chat.";

    private const float FarPlane = 9999999f;

    private static readonly string ChatPrefix = $" {ChatColors.Green}[NoFog]{ChatColors.Default}";

    private bool _noFogEnabled;

    private readonly Dictionary<uint, FogControllerState> _savedFogControllers = new();
    private readonly Dictionary<uint, GradientFogState> _savedGradientFog = new();
    private readonly Dictionary<uint, CubemapFogState> _savedCubemapFog = new();
    private readonly Dictionary<uint, PlayerVisibilityState> _savedPlayerVisibility = new();
    private readonly Dictionary<uint, SkyCameraFogState> _savedSkyCameraFog = new();

    private sealed record FogControllerState(bool Enable, float Start, float End, float Maxdensity, float Farz);
    private sealed record GradientFogState(bool Enabled, float FarZ, float FogStartDistance, float FogEndDistance, float FogMaxOpacity);
    private sealed record CubemapFogState(bool Active, float StartDistance, float EndDistance, float FogMaxOpacity);
    private sealed record PlayerVisibilityState(bool Enabled, float VisibilityStrength, float FogDistanceMultiplier, float FogMaxDensityMultiplier);
    private sealed record SkyCameraFogState(bool Enable, float Start, float End, float Maxdensity);

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
        // Entities are disabled in place rather than removed: env_fog_controller and
        // env_gradient_fog each own their own far-Z clip plane override (Farz/FarZ).
        // Deleting the entity drops that override entirely, so the engine falls back
        // to a much closer default far-Z plane and geometry beyond it renders black -
        // a harder cutoff than the original fog. Pushing Farz out instead keeps the
        // draw distance overridden while turning the actual fog effect off.
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CFogController>("env_fog_controller"))
        {
            if (!fog.IsValid)
                continue;

            if (!_savedFogControllers.ContainsKey(fog.Index))
            {
                _savedFogControllers[fog.Index] =
                    new FogControllerState(fog.Fog.Enable, fog.Fog.Start, fog.Fog.End, fog.Fog.Maxdensity, fog.Fog.Farz);
            }

            fog.Fog.Enable = false;
            fog.Fog.Start = FarPlane;
            fog.Fog.End = FarPlane;
            fog.Fog.Maxdensity = 0f;
            fog.Fog.Farz = FarPlane;
            Utilities.SetStateChanged(fog, "CFogController", "m_fog");
        }

        foreach (var gradient in Utilities.FindAllEntitiesByDesignerName<CGradientFog>("env_gradient_fog"))
        {
            if (!gradient.IsValid)
                continue;

            if (!_savedGradientFog.ContainsKey(gradient.Index))
            {
                _savedGradientFog[gradient.Index] = new GradientFogState(
                    gradient.IsEnabled, gradient.FarZ, gradient.FogStartDistance, gradient.FogEndDistance, gradient.FogMaxOpacity);
            }

            gradient.IsEnabled = false;
            gradient.FarZ = FarPlane;
            gradient.FogStartDistance = FarPlane;
            gradient.FogEndDistance = FarPlane;
            gradient.FogMaxOpacity = 0f;
            Utilities.SetStateChanged(gradient, "CGradientFog", "m_bIsEnabled");
        }

        foreach (var cubemap in Utilities.FindAllEntitiesByDesignerName<CEnvCubemapFog>("env_cubemap_fog"))
        {
            if (!cubemap.IsValid)
                continue;

            if (!_savedCubemapFog.ContainsKey(cubemap.Index))
            {
                _savedCubemapFog[cubemap.Index] = new CubemapFogState(
                    cubemap.Active, cubemap.StartDistance, cubemap.EndDistance, cubemap.FogMaxOpacity);
            }

            cubemap.Active = false;
            cubemap.StartDistance = FarPlane;
            cubemap.EndDistance = FarPlane;
            cubemap.FogMaxOpacity = 0f;
            Utilities.SetStateChanged(cubemap, "CEnvCubemapFog", "m_bActive");
        }

        foreach (var visibility in Utilities.FindAllEntitiesByDesignerName<CPlayerVisibility>("env_player_visibility"))
        {
            if (!visibility.IsValid)
                continue;

            if (!_savedPlayerVisibility.ContainsKey(visibility.Index))
            {
                _savedPlayerVisibility[visibility.Index] = new PlayerVisibilityState(
                    visibility.IsEnabled, visibility.VisibilityStrength, visibility.FogDistanceMultiplier, visibility.FogMaxDensityMultiplier);
            }

            visibility.IsEnabled = false;
            visibility.VisibilityStrength = 0f;
            visibility.FogDistanceMultiplier = FarPlane;
            visibility.FogMaxDensityMultiplier = 0f;
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_bIsEnabled");
        }

        // The 3D skybox fog lives on sky_camera; killing that entity would remove
        // the entire skybox, so zero out its fog parameters instead.
        foreach (var sky in Utilities.FindAllEntitiesByDesignerName<CSkyCamera>("sky_camera"))
        {
            if (!sky.IsValid)
                continue;

            var fog = sky.SkyboxData.Fog;

            if (!_savedSkyCameraFog.ContainsKey(sky.Index))
            {
                _savedSkyCameraFog[sky.Index] = new SkyCameraFogState(fog.Enable, fog.Start, fog.End, fog.Maxdensity);
            }

            fog.Enable = false;
            fog.Maxdensity = 0f;
            fog.Start = FarPlane;
            fog.End = FarPlane;
            Utilities.SetStateChanged(sky, "CSkyCamera", "m_skyboxData");
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
            fog.Fog.Maxdensity = saved.Maxdensity;
            fog.Fog.Farz = saved.Farz;
            Utilities.SetStateChanged(fog, "CFogController", "m_fog");
        }

        foreach (var gradient in Utilities.FindAllEntitiesByDesignerName<CGradientFog>("env_gradient_fog"))
        {
            if (!gradient.IsValid || !_savedGradientFog.TryGetValue(gradient.Index, out var saved))
                continue;

            gradient.IsEnabled = saved.Enabled;
            gradient.FarZ = saved.FarZ;
            gradient.FogStartDistance = saved.FogStartDistance;
            gradient.FogEndDistance = saved.FogEndDistance;
            gradient.FogMaxOpacity = saved.FogMaxOpacity;
            Utilities.SetStateChanged(gradient, "CGradientFog", "m_bIsEnabled");
        }

        foreach (var cubemap in Utilities.FindAllEntitiesByDesignerName<CEnvCubemapFog>("env_cubemap_fog"))
        {
            if (!cubemap.IsValid || !_savedCubemapFog.TryGetValue(cubemap.Index, out var saved))
                continue;

            cubemap.Active = saved.Active;
            cubemap.StartDistance = saved.StartDistance;
            cubemap.EndDistance = saved.EndDistance;
            cubemap.FogMaxOpacity = saved.FogMaxOpacity;
            Utilities.SetStateChanged(cubemap, "CEnvCubemapFog", "m_bActive");
        }

        foreach (var visibility in Utilities.FindAllEntitiesByDesignerName<CPlayerVisibility>("env_player_visibility"))
        {
            if (!visibility.IsValid || !_savedPlayerVisibility.TryGetValue(visibility.Index, out var saved))
                continue;

            visibility.IsEnabled = saved.Enabled;
            visibility.VisibilityStrength = saved.VisibilityStrength;
            visibility.FogDistanceMultiplier = saved.FogDistanceMultiplier;
            visibility.FogMaxDensityMultiplier = saved.FogMaxDensityMultiplier;
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_bIsEnabled");
        }

        foreach (var sky in Utilities.FindAllEntitiesByDesignerName<CSkyCamera>("sky_camera"))
        {
            if (!sky.IsValid || !_savedSkyCameraFog.TryGetValue(sky.Index, out var saved))
                continue;

            var fog = sky.SkyboxData.Fog;
            fog.Enable = saved.Enable;
            fog.Start = saved.Start;
            fog.End = saved.End;
            fog.Maxdensity = saved.Maxdensity;
            Utilities.SetStateChanged(sky, "CSkyCamera", "m_skyboxData");
        }

        ClearSavedState();
    }

    private void ClearSavedState()
    {
        _savedFogControllers.Clear();
        _savedGradientFog.Clear();
        _savedCubemapFog.Clear();
        _savedPlayerVisibility.Clear();
        _savedSkyCameraFog.Clear();
    }
}
