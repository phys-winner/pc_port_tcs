using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClosingShift.Android
{
    public static class AndroidInput
    {
        static readonly AndroidKeyState keys = new AndroidKeyState();
        static readonly AndroidPointerState pointerState = new AndroidPointerState();
        static readonly AndroidDialoguePress dialoguePress = new AndroidDialoguePress();
        static readonly bool[] held = keys.Held, down = keys.Down, up = keys.Up;
        static Vector2 pendingLook, look, touchMove, move;
        static float pendingScroll, scroll;
        static int frame = -1, blockedButtons, previousButtons, lastBack = -1, lookReadFrame=-1, scrollReadFrame=-1, captureStartFrame=-1;
        static bool touchMoving, analogActive, analogPreviouslyActive, analogApplied, blockMove, blockLook, blockTrigger;
        static bool triggerHeld, runTriggerHeld, suppressEscape, forceInventory, forceDocumentEscape;
        static Vector3 lastPhysicalMouse;
        static AndroidInputLifetime lifetime;
        static readonly Dictionary<string, KeyCode> bindings = new Dictionary<string, KeyCode> {
            {"Forward", KeyCode.W}, {"Backward", KeyCode.S}, {"Left", KeyCode.A}, {"Right", KeyCode.D},
            {"Run", KeyCode.LeftShift}, {"Crouch", KeyCode.C}, {"Interact", KeyCode.Mouse0}, {"Inventory", KeyCode.Tab}
        };
        internal static bool LastWasPad, PointerActive;
        static bool lastWasPhysical;
        static float sourceChangedAt=-1;
        internal static Vector2 UiNavigate;
        internal static bool UiSubmit, UiPointerHeld, UiPointerDown, UiPointerUp;
        internal static Vector3 PointerPosition;

        public static void SetVolume(float volume) { AudioListener.volume = Mathf.Clamp01(volume); }
        public static float GetVolume() { return AudioListener.volume; }
        public static bool RaycastInteraction(Ray ray,out RaycastHit hit,float range,int layers)
        { return AndroidInteractionRaycast.Raycast(ray,out hit,range,layers); }
        public static void CleanupExpiredOrderStickers(){AndroidContext.Ensure();AndroidQaOrders.CleanupInvalidOrders();}
        public static void BeginDialogueSentence(MonoBehaviour manager){dialoguePress.Begin(manager,Time.frameCount);}
        public static bool CanSkipDialogueSentence(MonoBehaviour manager)
        {return Application.platform!=RuntimePlatform.Android || dialoguePress.CanSkip(manager,Time.frameCount);}
        static readonly bool android = Application.platform == RuntimePlatform.Android;
        public static void Bootstrap()
        {
            // Game input reads reach this from every patched Input call; AndroidRenderScale
            // re-checks the native surface once per frame after the lifetime owner exists.
            if (!android || lifetime != null) return;
            AndroidSessionLog.Initialize();
            MobileRenderSettings.Initialize();
            var owner = new GameObject("AndroidInput");
            UnityEngine.Object.DontDestroyOnLoad(owner);
            lifetime = owner.AddComponent<AndroidInputLifetime>();
            Input.simulateMouseWithTouches = false;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            PointerPosition = new Vector3(Screen.width * .5f, Screen.height * .5f);
            lastPhysicalMouse = Input.mousePosition;
        }
        public static void SetKey(KeyCode key, bool value) { keys.Set(0, (int)key, value); }
        public static void CancelKey(KeyCode key) { keys.Cancel(0, (int)key); }
        public static KeyCode RecordBinding(KeyCode key, string action)
        {
            // None is significant: an intentionally unbound original action must stay unbound.
            if (action != null && bindings.ContainsKey(action)) bindings[action] = key;
            return key;
        }
        public static KeyCode KeyFor(string action, KeyCode fallback)
        { KeyCode key; return bindings.TryGetValue(action, out key) ? key : fallback; }
        public static void AddLookDelta(Vector2 delta) { if(frame==Time.frameCount && lookReadFrame!=frame && AndroidContext.CanLook)look+=delta;else pendingLook+=delta; }
        public static void AddScroll(float delta) { if(frame==Time.frameCount && scrollReadFrame!=frame)scroll+=delta;else pendingScroll+=delta; }
        public static void SetMove(Vector2 value, bool captured)
        {
            touchMove=Vector2.ClampMagnitude(value,1);touchMoving=captured;
            if(frame==Time.frameCount && AndroidContext.CanMove) {
                Vector2 pad=blockMove?Vector2.zero:AndroidController.Move;
                move=touchMove.sqrMagnitude>=pad.sqrMagnitude?touchMove:pad;
                bool owns=captured||move.sqrMagnitude>.0001f;analogActive=owns||analogPreviouslyActive;analogPreviouslyActive=owns;
            }
        }
        static void SignalSource(int source,bool intentional)
        {
            if(!intentional && Time.unscaledTime-sourceChangedAt<.35f)return;
            bool pad=source==1,physical=source==2;
            if(LastWasPad!=pad||lastWasPhysical!=physical)sourceChangedAt=Time.unscaledTime;
            LastWasPad=pad;lastWasPhysical=physical;
        }
        public static void NotifyTouch() { SignalSource(0,true); PointerActive = false; }
        public static void MovePointer(Vector2 delta)
        {
            PointerActive = true;
            PointerPosition.x = Mathf.Clamp(PointerPosition.x + delta.x, 0, Screen.width);
            PointerPosition.y = Mathf.Clamp(PointerPosition.y + delta.y, 0, Screen.height);
        }
        public static void RefreshUi() { AndroidUiInput.RefreshScene(); }
        public static void BeginKeyboardCapture(){captureStartFrame=Time.frameCount;}
        public static bool UseAuthoredRewrite(bool authored){return Application.platform!=RuntimePlatform.Android && authored;}
        public static bool GetPhysicalKeyDown(KeyCode key)
        {
            if(!Input.GetKeyDown(key))return false;
            if(Application.platform!=RuntimePlatform.Android)return true;
            if(Time.frameCount<=captureStartFrame)return false;
            if(key>=KeyCode.Mouse0 && key<=KeyCode.Mouse6 && AndroidUiInput.PointerOverPortOverlay())return false;
            return true;
        }
        public static void ContextChanged()
        {
            for (int source = 0; source <= 3; ++source) keys.CancelSource(source);
            pendingLook = look = touchMove = move = Vector2.zero; touchMoving = false;
            pendingScroll = scroll = 0; PointerActive = false;
            UiNavigate = Vector2.zero; UiSubmit = UiPointerHeld = UiPointerDown = UiPointerUp = false;
            pointerState.Held=pointerState.Down=pointerState.Up=false;
            AndroidUiInput.CancelPointers();
            blockedButtons |= AndroidController.Buttons;
            blockMove = blockLook = true; blockTrigger = true;
            if (lifetime != null) lifetime.ReleaseTouch();
        }
        public static void Reset()
        {
            ContextChanged(); AndroidController.Cancel();
        }
        static void PadKey(KeyCode key, bool value, bool pulse = false)
        {
            keys.Set(1, (int)key, value || pulse);
            if (!value) keys.Set(1, (int)key, false);
        }
        static bool Bit(int mask, int bit) { return (mask & (1 << bit)) != 0; }
        static void PadAction(string action, KeyCode fallback, bool value, bool pulse = false) { PadKey(KeyFor(action, fallback), value, pulse); }

        public static void EnsureFrame()
        {
            if (!android) return;
            int now = Time.frameCount;
            if (frame == now) return;
            Bootstrap();
            frame = now;
            suppressEscape = forceInventory = forceDocumentEscape = analogApplied = false;
            AndroidContext.Update();
            AndroidController.Poll();
            if (AndroidController.Changed) ContextChanged();
            if(AndroidController.Changed && !AndroidController.Connected && LastWasPad)SignalSource(0,true);
            blockedButtons &= AndroidController.Buttons;
            int buttons = AndroidController.Buttons & ~blockedButtons;
            int pressed = ((buttons & ~previousButtons) | AndroidController.Presses) & ~blockedButtons;
            previousButtons = buttons;
            if (blockMove && AndroidController.Move.sqrMagnitude < .001f) blockMove = false;
            if(blockLook && AndroidController.Look.sqrMagnitude<.001f)blockLook=false;
            if (blockTrigger && AndroidController.RightTrigger < .35f && AndroidController.LeftTrigger < .35f) blockTrigger = false;
            if(blockTrigger)pressed&=~((1<<15)|(1<<16));
            Vector2 padMove = blockMove ? Vector2.zero : AndroidController.Move;
            Vector2 padLook=blockLook?Vector2.zero:AndroidController.Look;
            triggerHeld = !blockTrigger && AndroidInputPolicy.Trigger(triggerHeld, AndroidController.RightTrigger);
            runTriggerHeld = !blockTrigger && AndroidInputPolicy.Trigger(runTriggerHeld, AndroidController.LeftTrigger);
            Vector2 nav = AndroidController.Navigate.sqrMagnitude > .1f ? AndroidController.Navigate : padMove;
            if(Mathf.Abs(nav.y)<.45f){if(Bit(pressed,11))nav.y=1;else if(Bit(pressed,12))nav.y=-1;}
            if(Mathf.Abs(nav.x)<.45f){if(Bit(pressed,13))nav.x=-1;else if(Bit(pressed,14))nav.x=1;}
            if (AndroidController.Activity && (pressed != 0 || padMove.sqrMagnitude > .04f || AndroidController.Look.sqrMagnitude > .04f || nav.sqrMagnitude > .04f || triggerHeld))SignalSource(1,pressed!=0);
            if (Input.anyKeyDown || Input.GetMouseButtonDown(0) || (Input.touchCount==0 && (Input.mousePosition-lastPhysicalMouse).sqrMagnitude>1)) { SignalSource(2,Input.anyKeyDown||Input.GetMouseButtonDown(0)); PointerActive = false; }
            lastPhysicalMouse = Input.mousePosition;
            for (int i = 0; i < Input.touchCount; ++i) {
                TouchPhase phase=Input.GetTouch(i).phase;
                if(phase==TouchPhase.Began)NotifyTouch();else if(phase==TouchPhase.Moved)SignalSource(0,false);
            }

            keys.CancelSource(1); keys.CancelSource(2); keys.CancelSource(3);
            UiSubmit = AndroidContext.NativeUI && Bit(pressed, 0);
            UiNavigate = AndroidContext.NativeUI ? new Vector2(Mathf.Abs(nav.x) > .45f ? Mathf.Sign(nav.x) : 0, Mathf.Abs(nav.y) > .45f ? Mathf.Sign(nav.y) : 0) : Vector2.zero;
            if (UiNavigate.sqrMagnitude > 0) PointerActive = false;
            if ((AndroidContext.NativeUI || (AndroidContext.Current == InputContext.World && !AndroidContext.CanLook && Cursor.lockState == CursorLockMode.None)) && padLook.sqrMagnitude > .01f)
                MovePointer(padLook*AndroidInputPolicy.RateDelta(Screen.height*.65f,Time.unscaledDeltaTime));
            pointerState.Advance(AndroidContext.NativeUI && PointerActive && Bit(buttons,0),AndroidContext.NativeUI && PointerActive && Bit(pressed,0));
            UiPointerHeld=pointerState.Held;UiPointerDown=pointerState.Down;UiPointerUp=pointerState.Up;

            if (!AndroidContext.NativeUI && AndroidContext.Current != InputContext.Rebind)
            {
                PadAction("Interact", KeyCode.Mouse0, Bit(buttons, 0) || triggerHeld, Bit(pressed, 0) || Bit(pressed,16));
                PadAction("Crouch", KeyCode.C, Bit(buttons, 2) || Bit(buttons, 7), Bit(pressed, 2) || Bit(pressed, 7));
                PadAction("Run", KeyCode.LeftShift, Bit(buttons, 6) || runTriggerHeld, Bit(pressed, 6) || Bit(pressed,15));
                PadAction("Inventory", KeyCode.Tab, Bit(buttons, 3) || Bit(buttons, 9), Bit(pressed, 3) || Bit(pressed, 9));
                if (AndroidContext.Current == InputContext.Choice)
                {
                    keys.Set(3, (int)AndroidContext.ChoiceKey(true), nav.y > .45f);
                    keys.Set(3, (int)AndroidContext.ChoiceKey(false), nav.y < -.45f);
                }
                else if (AndroidContext.Current == InputContext.Inventory || AndroidContext.Current == InputContext.Document)
                {
                    PadAction("Left", KeyCode.A, nav.x < -.45f || Bit(buttons, 4), Bit(pressed, 4));
                    PadAction("Right", KeyCode.D, nav.x > .45f || Bit(buttons, 5), Bit(pressed, 5));
                }
                else
                {
                    if (Bit(pressed, 4)) pendingScroll += 1;
                    if (Bit(pressed, 5)) pendingScroll -= 1;
                }
            }
            PadKey(KeyCode.Escape, Bit(buttons, 1) || Bit(buttons, 8), Bit(pressed, 1) || Bit(pressed, 8));
            if (lastBack < 0) lastBack = AndroidController.BackCounter;
            else if (lastBack != AndroidController.BackCounter) { lastBack = AndroidController.BackCounter; PadKey(KeyCode.Escape, false, true); }

            Vector2 combined = touchMove.sqrMagnitude >= padMove.sqrMagnitude ? touchMove : padMove;
            move = AndroidContext.CanMove ? Vector2.ClampMagnitude(combined, 1) : Vector2.zero;
            bool owns = AndroidContext.CanMove && (touchMoving || move.sqrMagnitude > .0001f);
            analogActive = owns || (analogPreviouslyActive && AndroidContext.CanMove);
            analogPreviouslyActive = owns;
            if (AndroidContext.CanMove)
            {
                keys.Set(2, (int)KeyFor("Forward", KeyCode.W), move.y > .01f);
                keys.Set(2, (int)KeyFor("Backward", KeyCode.S), move.y < -.01f);
                keys.Set(2, (int)KeyFor("Left", KeyCode.A), move.x < -.01f);
                keys.Set(2, (int)KeyFor("Right", KeyCode.D), move.x > .01f);
            }
            look = AndroidContext.CanLook ? pendingLook + padLook*AndroidInputPolicy.RateDelta(AndroidInputSettings.PadSensitivity,Time.unscaledDeltaTime) : Vector2.zero;
            pendingLook = Vector2.zero;
            scroll = pendingScroll; pendingScroll = 0;
            keys.Advance();
            if (down[(int)KeyCode.Escape] || Input.GetKeyDown(KeyCode.Escape)) RouteBack();
            AndroidUiInput.Update();
        }
        static void RouteBack()
        {
            if(AndroidUiInput.HasOpenDropdown()){AndroidUiInput.CloseDropdown();suppressEscape=true;return;}
            if(AndroidTouchOverlay.SettingsOpen){if(lifetime!=null)lifetime.CloseSettings();suppressEscape=true;return;}
            if(AndroidContext.TryExitInteraction()){suppressEscape=true;return;}
            AndroidInputPolicy.BackOwner owner = AndroidInputPolicy.ResolveBack(false,
                AndroidContext.Current == InputContext.Rebind, AndroidContext.NativeUI, AndroidContext.Current == InputContext.Document,
                AndroidContext.Current == InputContext.Inventory, AndroidContext.Current == InputContext.Choice || AndroidContext.Current == InputContext.Dialogue);
            switch (owner)
            {
                case AndroidInputPolicy.BackOwner.Dropdown: AndroidUiInput.CloseDropdown(); suppressEscape = true; break;
                case AndroidInputPolicy.BackOwner.Rebind:
                    if (AndroidContext.Mapper != null && AndroidContext.Mapper.Get("buttonText") != null) AndroidContext.Mapper.Invoke("BackRewrite");
                    suppressEscape = true; break;
                case AndroidInputPolicy.BackOwner.NativePanel: AndroidUiInput.Back(); suppressEscape = true; break;
                case AndroidInputPolicy.BackOwner.Document: forceDocumentEscape = true; suppressEscape = true; break;
                case AndroidInputPolicy.BackOwner.Inventory:
                    if(KeyFor("Inventory",KeyCode.Tab)!=KeyCode.None)forceInventory=true;
                    else if(AndroidContext.Inventory!=null)AndroidContext.Inventory.Invoke("InventoryClose");
                    suppressEscape = true; break;
                case AndroidInputPolicy.BackOwner.Story: suppressEscape = true; break;
            }
        }

        public static void ApplyAnalogMove(ref float x, ref float y, bool allowed)
        {
            EnsureFrame();
            bool keyboard = Input.GetKey(KeyFor("Forward", KeyCode.W)) || Input.GetKey(KeyFor("Backward", KeyCode.S)) || Input.GetKey(KeyFor("Left", KeyCode.A)) || Input.GetKey(KeyFor("Right", KeyCode.D));
            analogApplied = allowed && analogActive && !keyboard;
            if (analogApplied) { x = move.x; y = move.y; }
        }
        public static float DiagonalFactor(float original) { return analogApplied ? 1 : original; }
        public static string PromptKeyName(KeyCode key)
        {
            if(Application.platform!=RuntimePlatform.Android||lastWasPhysical||key==KeyCode.None)return key.ToString();
            bool ru=ClosingShift.Localization.RussianUiPresentation.RussianActive;
            if(key==KeyFor("Interact",KeyCode.Mouse0))return LastWasPad?"A / RT":ru?"ДЕЙСТВИЕ":"USE";
            if(key==KeyFor("Inventory",KeyCode.Tab))return LastWasPad?"Y / Select":ru?"ИНВЕНТАРЬ":"BAG";
            if(key==KeyFor("Run",KeyCode.LeftShift))return LastWasPad?"L2 / L3":ru?"БЕГ":"RUN";
            if(key==KeyFor("Crouch",KeyCode.C))return LastWasPad?"X / R3":ru?"ПРИСЕСТЬ":"CROUCH";
            if(key==KeyFor("Forward",KeyCode.W)||key==KeyFor("Backward",KeyCode.S)||key==KeyFor("Left",KeyCode.A)||key==KeyFor("Right",KeyCode.D))return ru?"ЛЕВЫЙ СТИК":"LEFT STICK";
            return key.ToString();
        }
        static bool State(bool[] values, KeyCode key)
        {
            EnsureFrame(); int index = (int)key;
            return index > 0 && index < values.Length && values[index];
        }
        static bool Forced(KeyCode key)
        { return key != KeyCode.None && forceInventory && key == KeyFor("Inventory",KeyCode.Tab); }
        public static bool GetKey(KeyCode key)
        {
            EnsureFrame();
            if(analogActive && AndroidContext.CanMove && key!=KeyCode.None) {
                if(key==KeyFor("Forward",KeyCode.W))return move.y>.01f||Input.GetKey(key);
                if(key==KeyFor("Backward",KeyCode.S))return move.y<-.01f||Input.GetKey(key);
                if(key==KeyFor("Left",KeyCode.A))return move.x<-.01f||Input.GetKey(key);
                if(key==KeyFor("Right",KeyCode.D))return move.x>.01f||Input.GetKey(key);
            }
            return State(held,key)||Input.GetKey(key);
        }
        public static bool GetKeyDown(KeyCode key)
        {
            bool virtualDown = State(down, key), virtualHeld = State(held, key);
            if (key == KeyCode.Escape && suppressEscape) return false;
            if(key==KeyCode.Mouse0 && KeyFor("Interact",KeyCode.Mouse0)!=KeyCode.Mouse0)
            {
                KeyCode mapped=KeyFor("Interact",KeyCode.Mouse0);
                if(mapped!=KeyCode.None && GetKeyDown(mapped) && AndroidContext.IsLegacyMouseAction())return true;
            }
            return Forced(key) || (Input.GetKeyDown(key) && (!virtualHeld || virtualDown)) || (virtualDown && !Input.GetKey(key));
        }
        public static bool GetReadingKeyDown(KeyCode key){EnsureFrame();return key==KeyCode.Escape && forceDocumentEscape || GetKeyDown(key);}
        public static bool GetKeyUp(KeyCode key) { return (State(up, key) && !Input.GetKey(key)) || (Input.GetKeyUp(key) && !State(held, key)); }
        public static bool GetKey(string key) { KeyCode code; return Enum.TryParse(key, true, out code) ? GetKey(code) : Input.GetKey(key); }
        public static bool GetKeyDown(string key) { KeyCode code; return Enum.TryParse(key, true, out code) ? GetKeyDown(code) : Input.GetKeyDown(key); }
        public static bool GetKeyUp(string key) { KeyCode code; return Enum.TryParse(key, true, out code) ? GetKeyUp(code) : Input.GetKeyUp(key); }
        public static bool GetMouseButton(int button) { return GetKey((KeyCode)((int)KeyCode.Mouse0 + button)); }
        public static bool GetMouseButtonDown(int button) { return GetKeyDown((KeyCode)((int)KeyCode.Mouse0 + button)); }
        public static bool GetMouseButtonUp(int button) { return GetKeyUp((KeyCode)((int)KeyCode.Mouse0 + button)); }
        static KeyCode ButtonKey(string name) { if (name == "Fire1") return KeyCode.Mouse0; if (name == "Fire2") return KeyCode.Mouse1; if (name == "Jump") return KeyCode.Space; return KeyCode.None; }
        public static bool GetButton(string name) { return State(held, ButtonKey(name)) || Input.GetButton(name); }
        public static bool GetButtonDown(string name)
        {
            KeyCode key = ButtonKey(name); bool virtualDown = State(down, key), virtualHeld = State(held, key);
            return (Input.GetButtonDown(name) && (!virtualHeld || virtualDown)) || (virtualDown && !Input.GetButton(name));
        }
        public static bool GetButtonUp(string name) { return (State(up, ButtonKey(name)) && !Input.GetButton(name)) || (Input.GetButtonUp(name) && !State(held, ButtonKey(name))); }
        public static Vector3 mousePosition { get { EnsureFrame(); return PointerActive ? PointerPosition : Input.mousePosition; } }
        public static Vector3 acceleration { get { return Input.acceleration; } }
        static float VirtualAxis(string name)
        {
            EnsureFrame();
            if (name == "Mouse X") {lookReadFrame=frame;return look.x;}
            if (name == "Mouse Y") {lookReadFrame=frame;return look.y;}
            if (name == "Mouse ScrollWheel") {scrollReadFrame=frame;return scroll;}
            if (name == "Horizontal") return move.x;
            if (name == "Vertical") return move.y;
            return 0;
        }
        public static float GetAxis(string name) { float value = NativeAxis(name, Input.GetAxis(name)) + VirtualAxis(name); return name == "Horizontal" || name == "Vertical" ? Mathf.Clamp(value, -1, 1) : value; }
        public static float GetAxisRaw(string name) { float value = NativeAxis(name, Input.GetAxisRaw(name)) + VirtualAxis(name); return name == "Horizontal" || name == "Vertical" ? Mathf.Clamp(value, -1, 1) : value; }
        static float NativeAxis(string name, float raw)
        { return AndroidNativeMousePolicy.Filter(name, raw, Application.platform == RuntimePlatform.Android, Input.mousePresent, Input.touchCount); }
        public static float GetPlayerLookAxis(string name)
        {
            EnsureFrame();
            float raw = Input.GetAxis(name);
            float virtualValue = AndroidContext.FixedCamera ? 0 : VirtualAxis(name);
            float result = NativeAxis(name, raw) + virtualValue;
#if TOUCH_PROBE
            AndroidTouchLeakProbe.Axis(name, raw, virtualValue, result, touchMoving, touchMove, look, pendingLook);
#endif
            return result;
        }
        public static Resolution[] GetResolutions()
        {
            return MobileRenderSettings.GetResolutions();
        }
        public static Resolution GetResolution(Resolution[] resolutions, int index)
        {
            return MobileRenderSettings.GetResolution(resolutions,index);
        }
    }
}
