using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ClosingShift.Android
{
    internal static class AndroidUiInput
    {
        sealed class Route
        {
            internal Button Button;
            internal Transform Scope;
            internal int Priority;
        }
        static Selectable[] selectables = new Selectable[0];
        static readonly List<Dropdown> dropdowns = new List<Dropdown>();
        static readonly List<Route> routes = new List<Route>();
        sealed class MenuPanel
        {
            internal RectTransform Panel;
            internal RectTransform[] Bounds;
            internal Vector3 Scale;
            internal Vector3 ParentScale;
            internal Vector2 ParentSize;
            internal float CanvasScale = -1;
            internal float Preference = -1;
            internal Rect SafeArea;
            internal int Width, Height;
        }
        static readonly List<MenuPanel> menus = new List<MenuPanel>();
        static readonly HashSet<Text> enlargedText = new HashSet<Text>(), subtitles = new HashSet<Text>();
        static readonly Vector3[] menuCorners = new Vector3[4];
        static readonly List<Selectable> visible = new List<Selectable>();
        static EventSystem events;
        static StandaloneInputModule module;
        static Transform scope;
        static int graphSignature;
        static GameObject lastSelected;
        static int scopePriority;
        static readonly List<RaycastResult> hits=new List<RaycastResult>();
        static AndroidQaSettingsHold qaSettings;
        internal static RectTransform FocusRect;
        internal static RectTransform OptionsPanel;
        static readonly HashSet<RectTransform> subtitleBackdrops = new HashSet<RectTransform>();

        internal static void RaiseSubtitle(Text text)
        {
            if(text==null || !subtitles.Add(text))return;
            text.rectTransform.anchoredPosition+=new Vector2(0,40);
            text.verticalOverflow=VerticalWrapMode.Overflow;
            Transform parent=text.transform.parent;
            RectTransform background=parent==null?null:parent.Find(text.name=="Subtitle"?"SubtitleBG":text.name=="Subtitle (VO)"?"SubtitleBG (VO)":text.name=="Subtitle (Ending)"?"SubtitleBG (Ending)":"SubtitleBG") as RectTransform;
            if(background!=null && subtitleBackdrops.Add(background))background.anchoredPosition+=new Vector2(0,40);
        }
        static bool InDropdown(Transform child)
        {
            // GetComponentInParent in this Unity version skips inactive templates.
            for(;child!=null;child=child.parent)if(child.GetComponent<Dropdown>()!=null)return true;
            return false;
        }
        static bool InScrollList(Transform child)
        {
            // Controls is inactive during preparation; Unity 2020 has no includeInactive parent overload.
            for(;child!=null;child=child.parent)if(child.GetComponent<ScrollRect>()!=null)return true;
            return false;
        }
        static void PrepareMenu(RectTransform panel)
        {
            enlargedText.RemoveWhere(text=>text==null);
            subtitles.RemoveWhere(text=>text==null);
            Transform controls=panel.Find("Background/TABS/TAB_CONTROLS");
            foreach(Text text in panel.GetComponentsInChildren<Text>(true))
            {
                // Enlarged row labels extend over their dropdown's hit area.
                if(text.name=="GraphicLabel")text.raycastTarget=false;
                if(InDropdown(text.transform))continue;
                if(controls!=null && text.transform.IsChildOf(controls) && InScrollList(text.transform))
                {
                    // Generated key rows retain the desktop prefab's tiny text transform.
                    text.rectTransform.localScale=Vector3.one;
                    text.fontSize=28;text.resizeTextForBestFit=true;text.resizeTextMinSize=24;text.resizeTextMaxSize=28;
                    text.horizontalOverflow=HorizontalWrapMode.Overflow;
                    enlargedText.Add(text);continue;
                }
                if(!enlargedText.Add(text))continue;
                int minimum=text.resizeTextForBestFit?text.resizeTextMinSize:text.fontSize;
                int maximum=text.resizeTextForBestFit?Mathf.Max(text.fontSize,text.resizeTextMaxSize):text.fontSize;
                text.fontSize=Mathf.RoundToInt(text.fontSize*1.22f);
                text.resizeTextForBestFit=true;text.resizeTextMinSize=minimum;text.resizeTextMaxSize=Mathf.RoundToInt(maximum*1.22f);
                if(text.name=="GraphicLabel")text.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,600);
            }
            foreach(Dropdown dropdown in panel.GetComponentsInChildren<Dropdown>(true))
            {
                foreach(Text text in new[]{dropdown.captionText,dropdown.itemText})
                {
                    if(text==null)continue;
                    // Authored dropdown labels use unrelated local scales and zero-size best-fit templates.
                    text.rectTransform.localScale=Vector3.one;
                    text.rectTransform.anchorMin=Vector2.zero;text.rectTransform.anchorMax=Vector2.one;
                    text.rectTransform.anchoredPosition=Vector2.zero;text.rectTransform.sizeDelta=new Vector2(-24,-4);
                    text.fontSize=20;text.resizeTextForBestFit=true;text.resizeTextMinSize=14;text.resizeTextMaxSize=20;
                    text.alignment=TextAnchor.MiddleLeft;text.raycastTarget=true;
                }
                RectTransform item=dropdown.itemText==null?null:dropdown.itemText.transform.parent as RectTransform;
                if(item!=null)item.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,34);
                if(dropdown.template!=null)dropdown.template.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,204);
            }
            foreach(Toggle toggle in panel.GetComponentsInChildren<Toggle>(true))
            {
                if(InDropdown(toggle.transform))continue;
                RectTransform row=toggle.transform.parent as RectTransform;
                if(row==null || row.GetComponent<AndroidToggleRow>()!=null)continue;
                Image hit=row.GetComponent<Image>();
                if(hit==null){hit=row.gameObject.AddComponent<Image>();hit.color=Color.clear;}
                hit.raycastTarget=true;
                // Authored labels are scaled beyond the row height and would steal adjacent row taps.
                foreach(Text label in row.GetComponentsInChildren<Text>(true))label.raycastTarget=false;
                row.gameObject.AddComponent<AndroidToggleRow>().Target=toggle;
                RectTransform target=toggle.transform as RectTransform;
                if(target!=null)target.sizeDelta=new Vector2(Mathf.Max(80,target.sizeDelta.x),Mathf.Max(32,target.sizeDelta.y));
                if(toggle.targetGraphic!=null)toggle.targetGraphic.rectTransform.localScale*=1.5f;
            }
        }

        internal static string Identity(Transform transform)
        {
            var parts = new List<string>();
            int buildIndex = transform.gameObject.scene.buildIndex;
            while (transform != null) { parts.Add(transform.name); transform = transform.parent; }
            parts.Reverse();
            return buildIndex + ":" + String.Join("\u001f", parts.ToArray());
        }
        internal static void RefreshScene()
        {
            var cached = new List<Selectable>();
            foreach (Selectable item in Resources.FindObjectsOfTypeAll<Selectable>())
                if (item != null && item.gameObject.scene.IsValid() &&
                    (item.gameObject.scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene() || item.transform.root.name == "AndroidInput")) cached.Add(item);
            selectables = cached.ToArray();
            dropdowns.Clear(); routes.Clear(); menus.Clear(); scope = null; graphSignature = 0; lastSelected = null;OptionsPanel=null;
            foreach (Selectable selectable in selectables)
            {
                if (selectable == null || !selectable.gameObject.scene.IsValid()) continue;
                Dropdown dropdown = selectable as Dropdown;
                if (dropdown != null) dropdowns.Add(dropdown);
                Button button = selectable as Button;
                if (button == null) continue;
                if (AndroidQaOrders.Available && AndroidTouchPolicy.PauseSettings(Identity(button.transform)))
                {
                    qaSettings = button.GetComponent<AndroidQaSettingsHold>();
                    if (qaSettings == null) qaSettings = button.gameObject.AddComponent<AndroidQaSettingsHold>();
                    qaSettings.Target = button;
                }
                AndroidUiRoutes.Entry entry;
                if (!AndroidUiRoutes.Back.TryGetValue(Identity(button.transform), out entry)) continue;
                Transform parent = button.transform;
                while (parent != null && Identity(parent) != entry.ScopeIdentity) parent = parent.parent;
                if (parent != null) routes.Add(new Route { Button = button, Scope = parent, Priority = entry.Priority });
                if (parent != null && parent.name == "OptionsPanel")
                {
                    RectTransform panel = parent as RectTransform, background = parent.Find("Background") as RectTransform;
                    if (panel != null && background != null)
                    {
                        OptionsPanel=panel;
                        PrepareMenu(panel);
                        var bounds = new List<RectTransform> { background };
                        // Footer controls extend below Background; hidden dropdown templates and list content do not define panel bounds.
                        foreach (string path in new[] { "Options_Buttons/Back", "Options_Buttons/Apply", "TABS/TAB_GENERAL/Default", "TABS/TAB_GRAPHIC/Default", "TABS/TAB_CONTROLS/Default" })
                        {
                            RectTransform footer = background.Find(path) as RectTransform;
                            if (footer != null) bounds.Add(footer);
                        }
                        menus.Add(new MenuPanel { Panel = panel, Bounds = bounds.ToArray(), Scale = panel.localScale });
                    }
                }
            }
            Install();
        }
        static void FitMenus()
        {
            foreach (MenuPanel menu in menus)
            {
                if (menu.Panel == null || !menu.Panel.gameObject.activeInHierarchy) continue;
                Canvas canvas = menu.Panel.GetComponentInParent<Canvas>();
                RectTransform parent = menu.Panel.parent as RectTransform;
                Vector3 parentScale = parent == null ? Vector3.one : parent.lossyScale;
                Vector2 parentSize = parent == null ? Vector2.zero : parent.rect.size;
                float canvasScale = canvas == null ? 1 : canvas.scaleFactor;
                // CanvasScaler may update in preWillRenderCanvases after this input frame, so resolution alone cannot cache the fit.
                if (menu.Width == Screen.width && menu.Height == Screen.height && menu.SafeArea == Screen.safeArea && menu.Preference == AndroidTouchOverlay.MenuScale &&
                    menu.ParentScale == parentScale && menu.ParentSize == parentSize && menu.CanvasScale == canvasScale) continue;
                menu.Panel.localScale = menu.Scale;
                Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                Rect bounds = MenuBounds(menu, camera);
                float fit = AndroidTouchPolicy.MenuFit(bounds.width, bounds.height, Screen.safeArea.width, Screen.safeArea.height, AndroidTouchOverlay.MenuScale);
                menu.Panel.localScale = menu.Scale * fit;
                bounds = MenuBounds(menu, camera);
                Vector2 center = bounds.center;
                Vector2 offset = new Vector2(AndroidTouchPolicy.MenuOffset(bounds.xMin, bounds.xMax, Screen.safeArea.xMin, Screen.safeArea.xMax),
                    AndroidTouchPolicy.MenuOffset(bounds.yMin, bounds.yMax, Screen.safeArea.yMin, Screen.safeArea.yMax));
                Vector2 current, target;
                if (parent != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, center, camera, out current) &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, center + offset, camera, out target))
                    menu.Panel.anchoredPosition += target - current;
                menu.Width = Screen.width; menu.Height = Screen.height; menu.SafeArea = Screen.safeArea; menu.Preference = AndroidTouchOverlay.MenuScale;
                menu.ParentScale = parent == null ? Vector3.one : parent.lossyScale;
                menu.ParentSize = parent == null ? Vector2.zero : parent.rect.size;
                menu.CanvasScale = canvas == null ? 1 : canvas.scaleFactor;
            }
        }
        static Rect MenuBounds(MenuPanel menu, Camera camera)
        {
            Vector2 minimum = new Vector2(Single.PositiveInfinity, Single.PositiveInfinity), maximum = new Vector2(Single.NegativeInfinity, Single.NegativeInfinity);
            foreach (RectTransform rect in menu.Bounds)
            {
                if (rect == null) continue;
                rect.GetWorldCorners(menuCorners);
                foreach (Vector3 corner in menuCorners)
                {
                    Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                    minimum = Vector2.Min(minimum, point); maximum = Vector2.Max(maximum, point);
                }
            }
            return Rect.MinMaxRect(minimum.x, minimum.y, maximum.x, maximum.y);
        }
        internal static void CancelPointers()
        {
            if (qaSettings != null) qaSettings.Cancel();
            if(events!=null && events.currentSelectedGameObject!=null)
                ExecuteEvents.Execute(events.currentSelectedGameObject,new PointerEventData(events){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerUpHandler);
            if(module!=null)module.DeactivateModule();
        }
        static void Install()
        {
            if (events == EventSystem.current && module != null) return;
            events = EventSystem.current;
            if (events == null) return;
            module = events.GetComponent<StandaloneInputModule>();
            if (module == null) return;
            AndroidBaseInput input = events.GetComponent<AndroidBaseInput>();
            if (input == null) input = events.gameObject.AddComponent<AndroidBaseInput>();
            module.inputOverride = input;
        }
        static bool Valid(Selectable item)
        {
            if (item == null || !item.IsActive() || !item.IsInteractable()) return false;
            for (Transform parent = item.transform; parent != null; parent = parent.parent)
            {
                CanvasGroup group = parent.GetComponent<CanvasGroup>();
                if (group != null && (group.alpha <= .01f || !group.interactable)) return false;
                Canvas canvas = parent.GetComponent<Canvas>();
                if (canvas != null && !canvas.enabled) return false;
            }
            return true;
        }
        static Transform Scope()
        {
            if(AndroidTouchOverlay.SettingsOpen){scopePriority=Int32.MaxValue;return AndroidTouchOverlay.SettingsScope;}
            Route best = null;
            foreach (Route route in routes)
                if (Valid(route.Button) && (best == null || route.Priority > best.Priority)) best = route;
            scopePriority=best==null?0:best.Priority;
            return best == null ? null : best.Scope;
        }
        static bool InScope(Selectable value, Transform root)
        {
            return root == null || value.transform == root || value.transform.IsChildOf(root);
        }
        internal static bool PointerOverPortOverlay()
        {
            if(EventSystem.current==null)return false;
            hits.Clear();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=Input.mousePosition},hits);
            return hits.Count!=0 && hits[0].gameObject.transform.root.name=="AndroidInput";
        }

        internal static bool HasOpenDropdown()
        {
            foreach (Dropdown dropdown in dropdowns)
            {
                if (dropdown == null || !dropdown.gameObject.activeInHierarchy) continue;
                Transform list = dropdown.transform.Find("Dropdown List");
                if (list != null && list.gameObject.activeInHierarchy) return true;
            }
            return false;
        }
        internal static bool CloseDropdown()
        {
            foreach (Dropdown dropdown in dropdowns)
            {
                if (dropdown == null || !dropdown.gameObject.activeInHierarchy) continue;
                Transform list = dropdown.transform.Find("Dropdown List");
                if (list == null || !list.gameObject.activeInHierarchy) continue;
                dropdown.OnCancel(new BaseEventData(EventSystem.current));
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(dropdown.gameObject);
                return true;
            }
            return false;
        }
        internal static bool Back()
        {
            Route best = null;
            foreach (Route route in routes)
                if (Valid(route.Button) && (best == null || route.Priority > best.Priority)) best = route;
            if (best != null) best.Button.onClick.Invoke();
            // Root-menu Back intentionally does not invoke Quit/NewGame/Continue.
            return AndroidContext.NativeUI;
        }

        internal static void Update()
        {
            Install();
            FitMenus();
            FocusRect = null;
            if (events == null || !AndroidContext.NativeUI || AndroidContext.Current == InputContext.Rebind || !AndroidInput.LastWasPad) return;
            Selectable current = events.currentSelectedGameObject == null ? null : events.currentSelectedGameObject.GetComponent<Selectable>();
            bool input = AndroidInput.UiNavigate.sqrMagnitude > .1f || AndroidInput.UiSubmit;
            if (HasOpenDropdown()) { if (current != null && Valid(current)) FocusRect = current.transform as RectTransform; return; }
            if (input || !Valid(current))
            {
                Transform nextScope = Scope();
                visible.Clear();
                foreach (Selectable value in selectables) if (Valid(value) && InScope(value, nextScope)) visible.Add(value);
                if (visible.Count != 0)
                {
                    int signature = Screen.width ^ (Screen.height << 12);
                    unchecked { foreach (Selectable item in visible) signature = signature * 31 + item.GetInstanceID(); }
                    if (signature != graphSignature) { BuildGraph(); graphSignature = signature; }
                    if (!Valid(current) || !InScope(current, nextScope) || scope != nextScope)
                    {
                        current = visible[0];
                        foreach (Selectable value in visible)
                            if (value.transform.position.y > current.transform.position.y + 2 ||
                                (Mathf.Abs(value.transform.position.y - current.transform.position.y) <= 2 && value.transform.position.x < current.transform.position.x)) current = value;
                        events.SetSelectedGameObject(current.gameObject);
                        ScrollIntoView(current);
                    }
                }
                scope = nextScope;
            }
            if (current != null && Valid(current)) FocusRect = current.transform as RectTransform;
            if (current != null && current.gameObject != lastSelected) { ScrollIntoView(current); lastSelected = current.gameObject; }
        }
        static void BuildGraph()
        {
            foreach (Selectable item in visible)
            {
                Navigation navigation = item.navigation;
                navigation.mode = Navigation.Mode.Explicit;
                navigation.selectOnUp = Neighbour(item, Vector2.up);
                navigation.selectOnDown = Neighbour(item, Vector2.down);
                navigation.selectOnLeft = Neighbour(item, Vector2.left);
                navigation.selectOnRight = Neighbour(item, Vector2.right);
                Slider slider = item as Slider;
                Scrollbar scrollbar = item as Scrollbar;
                bool horizontal = slider != null && (slider.direction == Slider.Direction.LeftToRight || slider.direction == Slider.Direction.RightToLeft) ||
                                  scrollbar != null && (scrollbar.direction == Scrollbar.Direction.LeftToRight || scrollbar.direction == Scrollbar.Direction.RightToLeft);
                if (slider != null || scrollbar != null)
                {
                    if (horizontal) navigation.selectOnLeft = navigation.selectOnRight = null;
                    else navigation.selectOnUp = navigation.selectOnDown = null;
                }
                item.navigation = navigation;
            }
        }
        static Selectable Neighbour(Selectable origin, Vector2 direction)
        {
            Selectable best = null;
            float score = 0;
            foreach (Selectable candidate in visible)
            {
                if (candidate == origin) continue;
                Vector2 delta = candidate.transform.position - origin.transform.position;
                float dot = Vector2.Dot(direction, delta);
                if (dot <= 1) continue;
                float value = dot / Mathf.Max(1, delta.sqrMagnitude);
                if (value > score) { score = value; best = candidate; }
            }
            return best;
        }
        internal static void ScrollIntoView(Selectable selected)
        {
            ScrollRect scroll = selected.GetComponentInParent<ScrollRect>();
            if (scroll == null || !scroll.vertical || scroll.content == null) return;
            RectTransform viewport = scroll.viewport != null ? scroll.viewport : scroll.transform as RectTransform;
            if (viewport == null) return;
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, selected.transform);
            float hidden = scroll.content.rect.height - viewport.rect.height;
            if (hidden <= 0) return;
            if (bounds.max.y > viewport.rect.yMax) scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition + (bounds.max.y - viewport.rect.yMax) / hidden);
            else if (bounds.min.y < viewport.rect.yMin) scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition + (bounds.min.y - viewport.rect.yMin) / hidden);
        }
    }

    public sealed class AndroidBaseInput : BaseInput
    {
        public override float GetAxisRaw(string name)
        {
            AndroidInput.EnsureFrame();
            if (AndroidContext.Current == InputContext.Rebind && (name == "Horizontal" || name == "Vertical")) return 0;
            if (AndroidContext.NativeUI)
            {
                if (name == "Horizontal") return Mathf.Clamp(base.GetAxisRaw(name) + AndroidInput.UiNavigate.x, -1, 1);
                if (name == "Vertical") return Mathf.Clamp(base.GetAxisRaw(name) + AndroidInput.UiNavigate.y, -1, 1);
            }
            return base.GetAxisRaw(name);
        }
        public override bool GetButtonDown(string name)
        {
            AndroidInput.EnsureFrame();
            if (name == "Cancel") return false;
            if (name == "Submit") return AndroidContext.NativeUI && AndroidContext.Current != InputContext.Rebind && ((AndroidInput.UiSubmit && !AndroidInput.PointerActive) || base.GetButtonDown(name));
            return base.GetButtonDown(name);
        }
        public override bool mousePresent { get { AndroidInput.EnsureFrame();return AndroidInput.PointerActive || base.mousePresent; } }
        public override Vector2 mousePosition { get { AndroidInput.EnsureFrame();return AndroidInput.PointerActive ? (Vector2)AndroidInput.PointerPosition : base.mousePosition; } }
        public override bool GetMouseButton(int button) { AndroidInput.EnsureFrame();return (button == 0 && AndroidContext.NativeUI && AndroidInput.PointerActive && AndroidInput.UiPointerHeld) || base.GetMouseButton(button); }
        public override bool GetMouseButtonDown(int button) { AndroidInput.EnsureFrame();return (button == 0 && AndroidContext.NativeUI && AndroidInput.PointerActive && AndroidInput.UiPointerDown) || base.GetMouseButtonDown(button); }
        public override bool GetMouseButtonUp(int button) { AndroidInput.EnsureFrame();return (button == 0 && AndroidContext.NativeUI && AndroidInput.PointerActive && AndroidInput.UiPointerUp) || base.GetMouseButtonUp(button); }
    }
}
