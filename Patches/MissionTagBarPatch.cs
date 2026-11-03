using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Makes the mission picker's row of filter tags scroll sideways when it no longer fits, which each
    /// custom map brings nearer, with a scrollbar copied from the mission list's.
    ///
    /// The panel as the game builds it (level2 and level4 alike): <c>Filter Tags</c>, 36 high across its top,
    /// holding the tags in a <c>HorizontalLayoutGroup</c> that keeps them to the right, and
    /// <c>Background</c> filling the rest. <c>Filter Tags</c> goes inside a new bar and viewport to be the
    /// <c>ScrollRect</c>'s content, list and items untouched. A row that fits looks as it did, and only a row
    /// that overflows makes the bar taller for the scrollbar (<see cref="MissionTagBarFit"/>).
    /// </summary>
    [HarmonyPatch(typeof(MissionSelectPanel), "Awake")]
    internal static class MissionTagBarPatch
    {
        static void Postfix(MissionSelectPanel __instance)
        {
            try { Build(__instance); }
            catch (Exception e) { Plugin.LogWarning($"mission picker: the tag row stays unscrollable ({e.GetType().Name}: {e.Message})"); }
        }

        static void Build(MissionSelectPanel panel)
        {
            var row = panel.tagFilterList != null ? panel.tagFilterList.transform as RectTransform : null;
            ScrollRect missions = panel.missionSelectList != null ? panel.missionSelectList.GetComponent<ScrollRect>() : null;
            RectTransform background = row != null ? row.parent.Find("Background") as RectTransform : null;
            Scrollbar scrollbar = missions != null ? missions.verticalScrollbar : null;
            Image rowImage = row != null ? row.GetComponent<Image>() : null;

            if (row == null || background == null || scrollbar == null || rowImage == null)
            {
                Plugin.LogWarning("mission picker: its panel is not laid out as expected, so the tag row stays unscrollable");
                return;
            }

            float height = row.rect.height > 0f ? row.rect.height : row.sizeDelta.y;

            var bar = new GameObject("Filter Tags Bar", typeof(RectTransform)).GetComponent<RectTransform>();
            bar.SetParent(row.parent, worldPositionStays: false);
            bar.SetSiblingIndex(row.GetSiblingIndex());
            bar.anchorMin = new Vector2(0f, 1f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(0.5f, 1f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(0f, height);

            // The row's background stays still: it moves from the row to the bar.
            Image barImage = bar.gameObject.AddComponent<Image>();
            barImage.sprite = rowImage.sprite;
            barImage.color = rowImage.color;
            barImage.material = rowImage.material;
            barImage.type = rowImage.type;
            barImage.pixelsPerUnitMultiplier = rowImage.pixelsPerUnitMultiplier;
            rowImage.enabled = false;

            var viewport = new GameObject("Viewport", typeof(RectTransform)).GetComponent<RectTransform>();
            viewport.SetParent(bar, worldPositionStays: false);
            viewport.anchorMin = new Vector2(0f, 1f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.pivot = new Vector2(0f, 1f);
            viewport.anchoredPosition = Vector2.zero;
            viewport.sizeDelta = new Vector2(0f, height);
            viewport.gameObject.AddComponent<RectMask2D>();

            row.SetParent(viewport, worldPositionStays: false);
            row.anchorMin = Vector2.zero;
            row.anchorMax = new Vector2(0f, 1f);
            row.pivot = new Vector2(0f, 1f);
            row.anchoredPosition = Vector2.zero;
            row.sizeDelta = new Vector2(viewport.rect.width, 0f);

            row.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            LayoutElement atLeastTheView = row.gameObject.AddComponent<LayoutElement>();

            Scrollbar copy = UnityEngine.Object.Instantiate(scrollbar, bar);
            copy.name = "Scrollbar Horizontal";
            copy.SetDirection(Scrollbar.Direction.LeftToRight, includeRectLayouts: false);
            var copyRect = (RectTransform)copy.transform;
            float thickness = ((RectTransform)scrollbar.transform).sizeDelta.x;
            copyRect.anchorMin = Vector2.zero;
            copyRect.anchorMax = new Vector2(1f, 0f);
            copyRect.pivot = new Vector2(0.5f, 0f);
            copyRect.anchoredPosition = Vector2.zero;
            copyRect.sizeDelta = new Vector2(0f, thickness);

            ScrollRect scroll = bar.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = row;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = missions.movementType;
            scroll.elasticity = missions.elasticity;
            scroll.inertia = missions.inertia;
            scroll.decelerationRate = missions.decelerationRate;
            scroll.scrollSensitivity = missions.scrollSensitivity;
            scroll.horizontalScrollbar = copy;
            scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            MissionTagBarFit fit = bar.gameObject.AddComponent<MissionTagBarFit>();
            fit.Bar = bar;
            fit.Viewport = viewport;
            fit.Row = row;
            fit.Background = background;
            fit.AtLeastTheView = atLeastTheView;
            fit.Height = height;
            fit.ScrollbarThickness = thickness;
        }
    }

    /// <summary>
    /// Keeps the row at least as wide as the view, so that its layout group goes on keeping a short row to
    /// the right as the game has it, and makes room for the scrollbar only while the row overflows.
    /// </summary>
    internal sealed class MissionTagBarFit : MonoBehaviour
    {
        internal RectTransform Bar, Viewport, Row, Background;
        internal LayoutElement AtLeastTheView;
        internal float Height, ScrollbarThickness;

        float _view = -1f;
        bool _overflow;

        void LateUpdate()
        {
            float view = Viewport.rect.width;
            if (view != _view)
            {
                _view = view;
                AtLeastTheView.minWidth = view;
            }

            bool overflow = Row.rect.width > view + 0.5f;
            if (overflow == _overflow) return;
            _overflow = overflow;

            float height = Height + (overflow ? ScrollbarThickness : 0f);
            Bar.sizeDelta = new Vector2(Bar.sizeDelta.x, height);
            Background.offsetMax = new Vector2(Background.offsetMax.x, -height);
        }
    }
}
