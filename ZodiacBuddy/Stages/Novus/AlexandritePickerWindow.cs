using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;

using Lumina.Data.Files;
using Lumina.Excel.Sheets;

using ZodiacBuddy.Stages.Novus.Data;

namespace ZodiacBuddy.Stages.Novus;

/// <summary>
/// Spot-picker overlay shown when an Alexandrite Map hint window opens.
/// Displays both candidate dig spots on the zone map and lets the player
/// choose which one to navigate to.
/// </summary>
internal sealed class AlexandritePickerWindow : Window, IDisposable
{
    private const float MapSize = 260f;
    private const float ZoomMin = 1f;
    private const float ZoomMax = 8f;

    private readonly Action<AlexandriteSpot> onNavigate;

    // Map texture cache: TerritoryId -> (wrap, sizeFactor); null wrap = load failed.
    private readonly Dictionary<uint, (IDalamudTextureWrap? Wrap, ushort SizeFactor)> texCache = new();

    private (AlexandriteSpot Spot1, AlexandriteSpot Spot2, string ZoneName)? pendingPick;

    // Zoom / pan state (UV-space: 0,0 = top-left of texture; 1,1 = bottom-right)
    private float zoom = 1f;       // current zoom level (1 = full map)
    private Vector2 uvOffset;      // top-left UV corner of the visible region

    /// <summary>
    /// Initializes a new instance of the <see cref="AlexandritePickerWindow"/> class.
    /// </summary>
    /// <param name="onNavigate">Callback invoked when the player selects a spot to navigate to.</param>
    public AlexandritePickerWindow(Action<AlexandriteSpot> onNavigate)
        : base("Navigate to Alexandrite Dig Spot", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.onNavigate = onNavigate;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(MapSize + 20, 200),
            MaximumSize = new Vector2(MapSize + 200, 600),
        };
    }

    /// <summary>
    /// Shows the picker for the two candidate spots in the given zone.
    /// </summary>
    /// <param name="spot1">First candidate spot.</param>
    /// <param name="spot2">Second candidate spot.</param>
    /// <param name="zoneName">Display name of the zone.</param>
    public void Show(AlexandriteSpot spot1, AlexandriteSpot spot2, string zoneName)
    {
        // Clear any stale cached texture for this territory so it is reloaded fresh.
        this.texCache.Remove(spot1.TerritoryId);
        this.pendingPick = (spot1, spot2, zoneName);
        this.zoom = 1f;
        this.uvOffset = Vector2.Zero;
        this.IsOpen = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var (wrap, _) in this.texCache.Values)
            wrap?.Dispose();
        this.texCache.Clear();
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        if (this.pendingPick is not { } pick)
        {
            this.IsOpen = false;
            return;
        }

        // --- Map image with zoom/pan ---
        var mapTex = this.GetMapTex(pick.Spot1.TerritoryId);
        var imageTopLeft = ImGui.GetCursorScreenPos();
        var mapRect = new Vector2(MapSize, MapSize);

        // Reserve the map area and capture input on it.
        ImGui.InvisibleButton("##maparea", mapRect);
        bool mapHovered = ImGui.IsItemHovered();

        // Scroll wheel → zoom, centred on the cursor.
        if (mapHovered)
        {
            var io = ImGui.GetIO();
            float scroll = io.MouseWheel;
            if (scroll != 0f)
            {
                float newZoom = Math.Clamp(this.zoom * (1f + scroll * 0.15f), ZoomMin, ZoomMax);
                // Keep the point under the cursor fixed in UV space.
                var mouseUv = (io.MousePos - imageTopLeft) / MapSize / this.zoom + this.uvOffset;
                float uvSize = 1f / newZoom;
                this.uvOffset = mouseUv - (io.MousePos - imageTopLeft) / MapSize * uvSize;
                this.zoom = newZoom;
            }

            // Left-drag → pan.
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
                this.uvOffset -= delta / MapSize / this.zoom;
            }
        }

        // Clamp uvOffset so the view cannot scroll outside the texture.
        float visibleUv = 1f / this.zoom;
        this.uvOffset = Vector2.Clamp(this.uvOffset, Vector2.Zero, new Vector2(1f - visibleUv));

        var uvMin = this.uvOffset;
        var uvMax = this.uvOffset + new Vector2(visibleUv);

        // Draw the (possibly zoomed/panned) texture via DrawList so we control UV.
        var dl = ImGui.GetWindowDrawList();
        if (mapTex.Wrap != null)
        {
            dl.AddImage(mapTex.Wrap.Handle, imageTopLeft, imageTopLeft + mapRect, uvMin, uvMax);
        }
        else
        {
            dl.AddRectFilled(imageTopLeft, imageTopLeft + mapRect, 0xFF303030);
        }

        // Overlay coloured dots for each spot, projected through current zoom/pan.
        {
            var sf = mapTex.SizeFactor;

            // Spot 1 — orange
            var uv1 = new Vector2(MapCoordToUV(pick.Spot1.MapX, sf), MapCoordToUV(pick.Spot1.MapY, sf));
            var pos1 = imageTopLeft + (uv1 - uvMin) / visibleUv * MapSize;
            if (IsInMapRect(pos1, imageTopLeft, mapRect))
            {
                dl.AddCircleFilled(pos1, 8f, 0xFF2166DE);
                dl.AddCircle(pos1, 8f, 0xFFFFFFFF, 0, 2f);
                dl.AddText(pos1 + new Vector2(10f, -8f), 0xFFFFFFFF, "1");
            }

            // Spot 2 — green
            var uv2 = new Vector2(MapCoordToUV(pick.Spot2.MapX, sf), MapCoordToUV(pick.Spot2.MapY, sf));
            var pos2 = imageTopLeft + (uv2 - uvMin) / visibleUv * MapSize;
            if (IsInMapRect(pos2, imageTopLeft, mapRect))
            {
                dl.AddCircleFilled(pos2, 8f, 0xFF45BA21);
                dl.AddCircle(pos2, 8f, 0xFFFFFFFF, 0, 2f);
                dl.AddText(pos2 + new Vector2(10f, -8f), 0xFFFFFFFF, "2");
            }
        }

        // Zoom hint
        ImGui.SetCursorScreenPos(imageTopLeft + new Vector2(4, MapSize - 18));
        ImGui.TextColored(new Vector4(0f, 0f, 0f, 1f), $"Zoom {this.zoom:F1}x  |  scroll to zoom · drag to pan");

        ImGui.Separator();

        // Spot 1 row
        ImGui.TextColored(new Vector4(0.87f, 0.40f, 0.13f, 1f), "\u25cf Spot 1");
        ImGui.SameLine();
        ImGui.TextUnformatted($"({pick.Spot1.MapX:F1}, {pick.Spot1.MapY:F1})");
        ImGui.SameLine(0, 16);
        if (ImGui.Button("Navigate##1"))
        {
            this.pendingPick = null;
            this.onNavigate(pick.Spot1);
        }

        // Spot 2 row
        ImGui.TextColored(new Vector4(0.13f, 0.73f, 0.27f, 1f), "\u25cf Spot 2");
        ImGui.SameLine();
        ImGui.TextUnformatted($"({pick.Spot2.MapX:F1}, {pick.Spot2.MapY:F1})");
        ImGui.SameLine(0, 16);
        if (ImGui.Button("Navigate##2"))
        {
            this.pendingPick = null;
            this.onNavigate(pick.Spot2);
        }

        ImGui.Separator();
        if (ImGui.Button("Cancel", new Vector2(-1, 0)))
        {
            this.pendingPick = null;
            this.IsOpen = false;
        }
    }

    /// <summary>
    /// Loads (and caches) a zone map texture for the given territory via DataManager,
    /// bypassing the async TextureProvider to get a synchronous result.
    /// </summary>
    private (IDalamudTextureWrap? Wrap, ushort SizeFactor) GetMapTex(uint territoryId)
    {
        if (this.texCache.TryGetValue(territoryId, out var hit))
            return hit;

        var territorySheet = Service.DataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet == null)
        {
            var r = ((IDalamudTextureWrap?)null, (ushort)100);
            this.texCache[territoryId] = r;
            return r;
        }

        var territory = territorySheet.GetRow(territoryId);
        var map = territory.Map.Value;
        var sizeFactor = map.SizeFactor;
        var id = map.Id.ExtractText();
        if (string.IsNullOrEmpty(id))
        {
            var r = ((IDalamudTextureWrap?)null, sizeFactor);
            this.texCache[territoryId] = r;
            return r;
        }

        // Saint Coinach path format: ui/map/{id}/{id.Replace("/","")}_m.tex
        // e.g. id="f1f2/00" => "ui/map/f1f2/00/f1f200_m.tex"
        var fileName = id.Replace("/", "");
        string? foundPath = null;
        foreach (var suffix in new[] { "_m", "_s" })
        {
            var candidate = $"ui/map/{id}/{fileName}{suffix}.tex";
            if (Service.DataManager.FileExists(candidate)) { foundPath = candidate; break; }
        }

        var texPath = foundPath ?? $"ui/map/{id}/{fileName}_m.tex";

        try
        {
            var texFile = Service.DataManager.GetFile<TexFile>(texPath);
            if (texFile == null)
            {
                Service.PluginLog.Warning($"[NovusMap] DataManager returned null for {texPath}");
                var r = ((IDalamudTextureWrap?)null, sizeFactor);
                this.texCache[territoryId] = r;
                return r;
            }

            // Lumina decodes the texture; CreateFromTexFile is the correct synchronous API.
            var wrap = Service.TextureProvider.CreateFromTexFile(texFile);
            var result = ((IDalamudTextureWrap?)wrap, sizeFactor);
            this.texCache[territoryId] = result;
            return result;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Warning($"[NovusMap] Texture load failed for {texPath}: {ex.Message}");
            var r = ((IDalamudTextureWrap?)null, sizeFactor);
            this.texCache[territoryId] = r;
            return r;
        }
    }

    /// <summary>
    /// Returns true when a screen-space point falls within the map display rectangle.
    /// Used to cull dot markers that have been panned off-screen.
    /// </summary>
    private static bool IsInMapRect(Vector2 point, Vector2 topLeft, Vector2 size)
        => point.X >= topLeft.X && point.Y >= topLeft.Y
        && point.X <= topLeft.X + size.X && point.Y <= topLeft.Y + size.Y;

    /// <summary>
    /// Converts a displayed map coordinate (1–41 scale) to a UV value [0, 1] for
    /// the zone's map texture, using the Map sheet's SizeFactor.
    /// </summary>
    private static float MapCoordToUV(float coord, ushort sizeFactor)
        => (coord - 1f) * (sizeFactor / 100f) / 41f;
}
