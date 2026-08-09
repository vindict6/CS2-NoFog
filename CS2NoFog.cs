using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2NoFog;

[MinimumApiVersion(200)]
public class CS2NoFogPlugin : BasePlugin
{
    public override string ModuleName => "CS2-NoFog";
    public override string ModuleVersion => "1.5.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Removes fog on any map when an admin types !nofog in chat.";

    private const float FarPlane = 9999999f;

    private static readonly string ChatPrefix = $" {ChatColors.Green}[NoFog]{ChatColors.Default}";

    // Every networked field of fogparams_t except the lerp/transition ones.
    // SetStateChanged on the containing field alone does NOT replicate nested
    // struct members - each one must be marked dirty at its own schema offset.
    private static readonly string[] FogparamFields =
    {
        "dirPrimary", "colorPrimary", "colorSecondary",
        "start", "end", "farz", "maxdensity", "exponent",
        "HDRColorScale", "skyboxFogFactor", "blendtobackground",
        "scattering", "locallightscale", "enable", "blend",
        "m_bNoReflectionFog",
    };

    private bool _noFogEnabled;

    private readonly Dictionary<uint, FogControllerState> _savedFogControllers = new();
    private readonly Dictionary<uint, GradientFogState> _savedGradientFog = new();
    private readonly Dictionary<uint, float> _savedCubemapFogOpacity = new();
    private readonly Dictionary<uint, PlayerVisibilityState> _savedPlayerVisibility = new();
    private readonly Dictionary<uint, SkyCameraFogState> _savedSkyCameraFog = new();

    private sealed record FogControllerState(bool Enable, float Start, float End, float Maxdensity, float Farz, float SkyboxFogFactor);
    private sealed record GradientFogState(bool Enabled, float FogMaxOpacity);
    private sealed record PlayerVisibilityState(bool Enabled, float VisibilityStrength, float FogDistanceMultiplier, float FogMaxDensityMultiplier);
    private sealed record SkyCameraFogState(bool Enable, float Start, float End, float Maxdensity, float SkyboxFogFactor);

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

    // No entity is ever deleted or deactivated here, deliberately:
    //  - Deleting env_fog_controller drops its far-Z override and the world clips
    //    to black at a close default plane.
    //  - Deleting env_cubemap_fog (or setting it inactive) leaves the client's
    //    cubemap fog pass running with no texture source, which renders as solid
    //    BLACK - the black wall at a fixed distance, and the black sky. Its one
    //    safe off-switch is FogMaxOpacity = 0 with the entity left fully intact.
    // Everything is neutralized in place with per-field dirty marking so the
    // changes actually reach clients.
    private void RemoveFog()
    {
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CFogController>("env_fog_controller"))
        {
            if (!fog.IsValid)
                continue;

            if (!_savedFogControllers.ContainsKey(fog.Index))
            {
                _savedFogControllers[fog.Index] = new FogControllerState(
                    fog.Fog.Enable, fog.Fog.Start, fog.Fog.End, fog.Fog.Maxdensity, fog.Fog.Farz,
                    GetSkyboxFogFactor(fog.Fog));
            }

            // Fog stays enabled so its far-Z override keeps applying; density zero
            // and huge distances make it invisible.
            fog.Fog.Enable = true;
            fog.Fog.Start = FarPlane;
            fog.Fog.End = FarPlane;
            fog.Fog.Maxdensity = 0f;
            fog.Fog.Farz = FarPlane;
            SetSkyboxFogFactor(fog.Fog, 0f);
            MarkFogparamsChanged(fog, "CFogController", "m_fog");
        }

        foreach (var gradient in Utilities.FindAllEntitiesByDesignerName<CGradientFog>("env_gradient_fog"))
        {
            if (!gradient.IsValid)
                continue;

            if (!_savedGradientFog.ContainsKey(gradient.Index))
                _savedGradientFog[gradient.Index] = new GradientFogState(gradient.IsEnabled, gradient.FogMaxOpacity);

            gradient.IsEnabled = false;
            gradient.FogMaxOpacity = 0f;
            Utilities.SetStateChanged(gradient, "CGradientFog", "m_bIsEnabled");
            Utilities.SetStateChanged(gradient, "CGradientFog", "m_flFogMaxOpacity");
        }

        foreach (var cubemap in Utilities.FindAllEntitiesByDesignerName<CEnvCubemapFog>("env_cubemap_fog"))
        {
            if (!cubemap.IsValid)
                continue;

            if (!_savedCubemapFogOpacity.ContainsKey(cubemap.Index))
                _savedCubemapFogOpacity[cubemap.Index] = cubemap.FogMaxOpacity;

            cubemap.FogMaxOpacity = 0f;
            Utilities.SetStateChanged(cubemap, "CEnvCubemapFog", "m_flFogMaxOpacity");
        }

        foreach (var visibility in Utilities.FindAllEntitiesByDesignerName<CPlayerVisibility>("env_player_visibility"))
        {
            if (!visibility.IsValid)
                continue;

            if (!_savedPlayerVisibility.ContainsKey(visibility.Index))
            {
                _savedPlayerVisibility[visibility.Index] = new PlayerVisibilityState(
                    visibility.IsEnabled, visibility.VisibilityStrength,
                    visibility.FogDistanceMultiplier, visibility.FogMaxDensityMultiplier);
            }

            visibility.IsEnabled = false;
            visibility.VisibilityStrength = 0f;
            visibility.FogDistanceMultiplier = 1f;
            visibility.FogMaxDensityMultiplier = 0f;
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_bIsEnabled");
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_flVisibilityStrength");
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_flFogDistanceMultiplier");
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_flFogMaxDensityMultiplier");
        }

        // The 3D skybox fog lives on sky_camera. Each pawn's networked skybox
        // params are refreshed from this entity every tick, so neutralizing the
        // source covers all players automatically.
        foreach (var sky in Utilities.FindAllEntitiesByDesignerName<CSkyCamera>("sky_camera"))
        {
            if (!sky.IsValid)
                continue;

            var fog = sky.SkyboxData.Fog;

            if (!_savedSkyCameraFog.ContainsKey(sky.Index))
            {
                _savedSkyCameraFog[sky.Index] = new SkyCameraFogState(
                    fog.Enable, fog.Start, fog.End, fog.Maxdensity, GetSkyboxFogFactor(fog));
            }

            fog.Enable = false;
            fog.Maxdensity = 0f;
            fog.Start = FarPlane;
            fog.End = FarPlane;
            SetSkyboxFogFactor(fog, 0f);
            MarkSkyCameraFogChanged(sky);
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
            SetSkyboxFogFactor(fog.Fog, saved.SkyboxFogFactor);
            MarkFogparamsChanged(fog, "CFogController", "m_fog");
        }

        foreach (var gradient in Utilities.FindAllEntitiesByDesignerName<CGradientFog>("env_gradient_fog"))
        {
            if (!gradient.IsValid || !_savedGradientFog.TryGetValue(gradient.Index, out var saved))
                continue;

            gradient.IsEnabled = saved.Enabled;
            gradient.FogMaxOpacity = saved.FogMaxOpacity;
            Utilities.SetStateChanged(gradient, "CGradientFog", "m_bIsEnabled");
            Utilities.SetStateChanged(gradient, "CGradientFog", "m_flFogMaxOpacity");
        }

        foreach (var cubemap in Utilities.FindAllEntitiesByDesignerName<CEnvCubemapFog>("env_cubemap_fog"))
        {
            if (!cubemap.IsValid || !_savedCubemapFogOpacity.TryGetValue(cubemap.Index, out var opacity))
                continue;

            cubemap.FogMaxOpacity = opacity;
            Utilities.SetStateChanged(cubemap, "CEnvCubemapFog", "m_flFogMaxOpacity");
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
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_flVisibilityStrength");
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_flFogDistanceMultiplier");
            Utilities.SetStateChanged(visibility, "CPlayerVisibility", "m_flFogMaxDensityMultiplier");
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
            SetSkyboxFogFactor(fog, saved.SkyboxFogFactor);
            MarkSkyCameraFogChanged(sky);
        }

        ClearSavedState();
    }

    private static void MarkFogparamsChanged(CBaseEntity entity, string className, string fieldName, int extraOffset = 0)
    {
        foreach (var field in FogparamFields)
        {
            Utilities.SetStateChanged(entity, className, fieldName,
                extraOffset + Schema.GetSchemaOffset("fogparams_t", field));
        }
    }

    private static void MarkSkyCameraFogChanged(CSkyCamera sky)
    {
        MarkFogparamsChanged(sky, "CSkyCamera", "m_skyboxData",
            Schema.GetSchemaOffset("sky3dparams_t", "fog"));
    }

    // skyboxFogFactor is not surfaced as a typed property, so go through the
    // schema system directly.
    private static float GetSkyboxFogFactor(fogparams_t fog)
        => Schema.GetRef<float>(fog.Handle, "fogparams_t", "skyboxFogFactor");

    private static void SetSkyboxFogFactor(fogparams_t fog, float value)
        => Schema.GetRef<float>(fog.Handle, "fogparams_t", "skyboxFogFactor") = value;

    private void ClearSavedState()
    {
        _savedFogControllers.Clear();
        _savedGradientFog.Clear();
        _savedCubemapFogOpacity.Clear();
        _savedPlayerVisibility.Clear();
        _savedSkyCameraFog.Clear();
    }
}
