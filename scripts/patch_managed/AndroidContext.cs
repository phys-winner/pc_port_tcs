using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ClosingShift.Android
{
    internal enum InputContext { Menu, World, Inventory, Document, Dialogue, Choice, Rebind }

    internal static class AndroidContext
    {
        internal sealed class View
        {
            internal readonly MonoBehaviour Owner;
            readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
            readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();
            internal View(MonoBehaviour owner)
            {
                Owner = owner;
                foreach (FieldInfo f in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) fields[f.Name] = f;
                foreach (MethodInfo m in owner.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.GetParameters().Length == 0) methods[m.Name] = m;
            }
            internal object Get(string name) { FieldInfo f; return Owner != null && fields.TryGetValue(name, out f) ? f.GetValue(Owner) : null; }
            internal void Set(string name, object value) { FieldInfo f; if (Owner != null && fields.TryGetValue(name, out f)) f.SetValue(Owner, value); }
            internal bool Flag(string name) { object value = Get(name); return value is bool && (bool)value; }
            internal bool ActiveObject(string name) { GameObject value = Get(name) as GameObject; return value != null && value.activeInHierarchy; }
            internal void Invoke(string name) { MethodInfo m; if (Owner != null && methods.TryGetValue(name, out m)) m.Invoke(Owner, null); }
        }

        static bool initialized;
        static int scene = Int32.MinValue;
        internal static View Game, Player, Look, Inventory, Reading, Dialogue, Mapper, Interact;
        static View openingDialogue;
        static View photoReader;
        static readonly AndroidReadingClose photoClose=new AndroidReadingClose();
        static readonly List<View> fixedCameras = new List<View>();
        static readonly List<View> slideshows = new List<View>(), extraMouse = new List<View>();
        static readonly List<View> switchedCameras = new List<View>(), securityCameras = new List<View>();
        static View exitInteraction;
        static string exitMethod;
        static readonly List<View> choiceOwners = new List<View>();
        static readonly Dictionary<Type,bool> interactionTypes = new Dictionary<Type,bool>();
        static GameObject interactionTarget;
        static MonoBehaviour[] interactionComponents = new MonoBehaviour[0];
        static Text hoveredChoice;
        static Color originalChoiceColor;
        internal static RectTransform ChoiceFocus { get { return hoveredChoice == null ? null : hoveredChoice.rectTransform; } }
        static bool choiceDescending=true;
        internal static InputContext Current;
        internal static bool NativeUI, CanLook, CanMove, FixedCamera, CanPause;
        internal static bool CanInteract
        {
            get
            {
                if(Current!=InputContext.World)return false;
                if(exitInteraction!=null || !CanMove && IsLegacyMouseAction())return true;
                if(Interact!=null && Interact.Owner!=null && Interact.Owner.isActiveAndEnabled && !Interact.Flag("inUse"))
                {
                    GameObject target=Interact.Get("RaycastObject") as GameObject;
                    if(target!=interactionTarget)
                    {
                        interactionTarget=target;
                        interactionComponents=target==null?new MonoBehaviour[0]:target.GetComponents<MonoBehaviour>();
                    }
                    // The original raycast also hits ordinary walls with no interaction script.
                    if(target!=null)foreach(MonoBehaviour component in interactionComponents)
                    {
                        if(component==null || !component.isActiveAndEnabled)continue;
                        Type type=component.GetType();bool usable;
                        if(!interactionTypes.TryGetValue(type,out usable))interactionTypes[type]=usable=AndroidTouchPolicy.HasInteraction(type);
                        if(usable)return true;
                    }
                }
                Text message=Inventory==null?null:Inventory.Get("interactMessage") as Text;
                return Inventory!=null && Inventory.Get("itemInHand") as UnityEngine.Object != null && message!=null && message.isActiveAndEnabled && !String.IsNullOrEmpty(message.text);
            }
        }
        internal static int Revision;
        internal static bool IntroPending
        {
            get { return openingDialogue != null && openingDialogue.Owner != null &&
                AndroidTouchPolicy.OpeningDialoguePending(openingDialogue.Owner.gameObject.activeInHierarchy, openingDialogue.Flag("completed")); }
        }

        internal static void Ensure()
        {
            if (!initialized) { initialized = true; SceneManager.sceneLoaded += SceneLoaded; AndroidInputSettings.Load(); }
            if (scene != SceneManager.GetActiveScene().handle) Scan();
        }
        static void SceneLoaded(Scene loaded, LoadSceneMode mode) { Scan(); AndroidInput.ContextChanged(); }
        static void Scan()
        {
            scene = SceneManager.GetActiveScene().handle;
            Game = Player = Look = Inventory = Reading = Dialogue = Mapper = Interact = openingDialogue = null;
            photoReader=null;photoClose.Reset();
            ClearChoiceHover(); fixedCameras.Clear();slideshows.Clear();extraMouse.Clear();choiceOwners.Clear();
            switchedCameras.Clear();securityCameras.Clear();exitInteraction=null;exitMethod=null;
            AndroidGraphics.BeginScene();
            AndroidBusAudioProbe.BeginScene();
            selectors.Clear(); dialogueSystemPresent = false;
            foreach (MonoBehaviour component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (component == null || !component.gameObject.scene.IsValid()) continue;
                ObserveDialogueSystem(component);
                if (component.gameObject.scene.handle != scene) continue;
                string name = component.GetType().FullName;
                AndroidGraphics.Observe(component, name);
                AndroidBusAudioProbe.Observe(component, name);
                switch (name)
                {
                    case "HFPS_GameManager": if (Game == null || component.isActiveAndEnabled) Game = new View(component); break;
                    case "PlayerController": if (Player == null || component.isActiveAndEnabled) Player = new View(component); break;
                    case "MouseLook": if (Look == null || component.isActiveAndEnabled) Look = new View(component); break;
                    case "AM_Inventory": Inventory = new View(component); break;
                    case "ReadingMaterialsManager": Reading = new View(component); break;
                    case "ReadingMaterials":
                        if(AndroidUiInput.Identity(component.transform)=="1:---------Sequences---------\u001fImportant props\u001flook at photo of mc")photoReader=new View(component);
                        break;
                    case "DialogueManager": Dialogue = new View(component); break;
                    case "DialogueTrigger":
                        if (AndroidTouchPolicy.IsOpeningDialogue(AndroidUiInput.Identity(component.transform))) openingDialogue = new View(component);
                        break;
                    case "DialogueYesNo": choiceOwners.Add(new View(component)); break;
                    case "InputController": if (Mapper == null || component.isActiveAndEnabled) Mapper = new View(component); break;
                    case "InteractManager":Interact=new View(component);break;
                    // Its OnGUI only draws debug stats; the IMGUI layout pass allocated every frame.
                    case "LuxWater.LuxWater_ProjectorRenderer": component.useGUILayout = false; break;
                    case "SlideShow_DialogueTrigger":slideshows.Add(new View(component));break;
                    case "ActivationScript":
                        FieldInfo input=component.GetType().GetField("inputActivation");
                        FieldInfo extra=component.GetType().GetField("extraInput");
                        if(input!=null && extra!=null && (bool)input.GetValue(component) && (KeyCode)extra.GetValue(component)==KeyCode.Mouse0)extraMouse.Add(new View(component));
                        break;
                    case "FixedRotateCamera": fixedCameras.Add(new View(component)); break;
                    case "PlayerSwitchCamLookAtObj": switchedCameras.Add(new View(component)); break;
                    case "SecurityCameras": securityCameras.Add(new View(component)); break;
                }
            }
            HideDesktopTutorials();
            DisableIdleSelectors();
            AndroidRoomCulling.Prepare();
            AndroidRecipeText.Prepare();
            AndroidUiInput.RaiseSubtitle(Game==null?null:Game.Get("Subtitle") as Text);
            AndroidUiInput.RaiseSubtitle(Dialogue==null?null:Dialogue.Get("dialogueText") as Text);
            AndroidGraphics.Enforce();
            AndroidUiInput.RefreshScene();
            Debug.Log("PORT_INPUT cached scene=" + SceneManager.GetActiveScene().buildIndex + " player=" + (Player != null) + " openingIntro=" + (openingDialogue != null) + " authored_actions=8");
        }

        static readonly List<Behaviour> selectors = new List<Behaviour>();
        static bool dialogueSystemPresent;

        static bool DerivesFrom(Type type, string baseName)
        {
            for (; type != null; type = type.BaseType) if (type.FullName == baseName) return true;
            return false;
        }

        // The unused Pixel Crushers package left a Selector on the player. Without a
        // DialogueSystemController its IsUseButtonDown repeats Object.FindObjectOfType every
        // frame and always returns false; without any Usable it can never select or draw.
        static void ObserveDialogueSystem(MonoBehaviour component)
        {
            Type type = component.GetType();
            string name = type.FullName;
            if (name.StartsWith("PixelCrushers.DialogueSystem.", StringComparison.Ordinal))
            {
                if (DerivesFrom(type, "PixelCrushers.DialogueSystem.Selector") || DerivesFrom(type, "PixelCrushers.DialogueSystem.ProximitySelector"))
                    selectors.Add(component);
                else if (DerivesFrom(type, "PixelCrushers.DialogueSystem.DialogueSystemController") || DerivesFrom(type, "PixelCrushers.DialogueSystem.Usable"))
                    dialogueSystemPresent = true;
            }
        }

        static void DisableIdleSelectors()
        {
            if (Application.platform != RuntimePlatform.Android || dialogueSystemPresent) return;
            int disabled = 0;
            foreach (Behaviour selector in selectors)
                if (selector != null && selector.enabled) { selector.enabled = false; ++disabled; }
            if (disabled > 0) Debug.Log("PORT_PERF dialogue selectors disabled=" + disabled + " reason=no-controller-no-usable");
        }

        static void HideDesktopTutorials()
        {
            if (Application.platform != RuntimePlatform.Android || Game == null || Game.Owner == null) return;
            foreach (string path in new[] { "Canvas (Options menu)/Show tutorial/tutorial", "Canvas (Options menu)/UI/PauseUI/tutorial" })
            {
                Transform tutorial = Game.Owner.transform.Find(path);
                if (tutorial == null) continue;
                bool icons = tutorial.Find("mouse") != null && tutorial.Find("wasd") != null && tutorial.Find("tab") != null &&
                    tutorial.Find("Shift") != null && tutorial.Find("C") != null;
                // Hide only the keyboard artwork; its authored parent still runs its animation/events.
                if (AndroidTouchPolicy.IsKeyboardTutorial(path, icons)) tutorial.gameObject.SetActive(false);
            }
        }

        internal static void Update()
        {
            Ensure();
            AndroidBusAudioProbe.Update();
            if(photoClose.Sample(photoReader!=null && photoReader.Flag("reading")) && Inventory!=null)
            {
                IList items=Inventory.Get("ItemsInInventory") as IList;
                if(items!=null)foreach(object item in items)
                {
                    MonoBehaviour component=item as MonoBehaviour;
                    if(component!=null && component.name=="Photo")
                    { AndroidQaOrders.UseInventoryItem(item);Debug.Log("PORT_READING photo consumed after close");break; }
                }
            }
            FixedCamera = false;
            foreach (View camera in fixedCameras) if (camera.Owner != null && camera.Owner.isActiveAndEnabled && camera.Flag("activated")) { FixedCamera = true; break; }
            exitInteraction=null;exitMethod=null;
            foreach(View camera in switchedCameras)
                if(camera.Owner!=null && camera.Owner.isActiveAndEnabled && camera.Flag("switched")) {exitInteraction=camera;exitMethod="GetOut";break;}
            foreach(View camera in securityCameras)
                if(camera.Owner!=null && camera.Owner.isActiveAndEnabled && camera.Flag("isIn")) {exitInteraction=camera;exitMethod="ExitPeep";FixedCamera=true;break;}
            bool menu = SceneManager.GetActiveScene().buildIndex == 0 || Player == null;
            bool rebind = Mapper != null && Mapper.Flag("rebind");
            bool document = Reading != null && Reading.Flag("reading");
            bool inventory = Inventory != null && (Inventory.Flag("InventoryIsOpen") || Inventory.Flag("isOpen"));
            bool choice = Dialogue != null && Dialogue.ActiveObject("YesNoPar");
            bool talking = Dialogue != null && Dialogue.Flag("dialogueShown");
            NativeUI = rebind || menu || (Game != null && (Game.Flag("isPaused") || Game.ActiveObject("PlayerDeadPanel")));
            InputContext next = rebind ? InputContext.Rebind : NativeUI ? InputContext.Menu : document ? InputContext.Document : inventory ? InputContext.Inventory : choice ? InputContext.Choice : talking ? InputContext.Dialogue : InputContext.World;
            if(next==InputContext.Choice && Current!=InputContext.Choice)ResolveChoiceOrder();
            if(next!=InputContext.Choice)ClearChoiceHover();
            bool canLook = !NativeUI && !document && !inventory && (FixedCamera || (Look != null && Look.Owner != null && Look.Owner.isActiveAndEnabled && Cursor.lockState == CursorLockMode.Locked && Time.timeScale > 0));
            bool canMove = !NativeUI && !document && !inventory && Player != null && Player.Flag("controllable");
            if (next != Current || canLook != CanLook || canMove != CanMove) { ++Revision; Current = next; CanLook = canLook; CanMove = canMove; AndroidInput.ContextChanged(); }
            CanPause = NativeUI || rebind || document || inventory || (Game != null && !Game.Flag("disablePause") && !Game.Flag("isPlayerLocked") && !Game.Flag("isPlayerCaught"));
        }

        static void ResolveChoiceOrder()
        {
            IList choices = Dialogue == null ? null : Dialogue.Get("choiceTexts") as IList;
            choiceDescending = true;
            if (choices != null && choices.Count >= 2)
            {
                Text first = choices[0] as Text, second = choices[1] as Text;
                if (first != null && second != null) choiceDescending = first.transform.position.y >= second.transform.position.y;
            }
        }
        internal static void ClearChoiceHover()
        {
            if (hoveredChoice != null) hoveredChoice.color = originalChoiceColor;
            hoveredChoice = null;
        }
        internal static bool HoverChoice(Vector2 position)
        {
            IList choices = Dialogue == null ? null : Dialogue.Get("choiceTexts") as IList;
            View active = null;
            foreach (View owner in choiceOwners) if (owner.Owner != null && owner.Owner.isActiveAndEnabled && owner.Flag("show")) { active = owner; break; }
            if (choices == null || active == null) return false;
            IList actual = active.Get("choiceTexts") as IList;
            int count = actual == null ? 0 : Math.Min(actual.Count, choices.Count);
            for (int i = 0; i < count; ++i)
            {
                Text text = choices[i] as Text;
                if (text == null || !text.isActiveAndEnabled || String.IsNullOrEmpty(text.text)) continue;
                Canvas canvas = text.canvas;
                Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                Vector2 local;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(text.rectTransform, position, camera, out local)) continue;
                Rect hit = text.rectTransform.rect;
                hit.xMin -= 12; hit.xMax += 12; hit.yMin -= 8; hit.yMax += 8;
                if (!hit.Contains(local)) continue;
                if (hoveredChoice != text)
                {
                    ClearChoiceHover(); hoveredChoice = text; originalChoiceColor = text.color;
                    text.color = new Color(1, .83f, .36f, originalChoiceColor.a);
                }
                active.Set("currentChoice", i);
                GameObject pointer = Dialogue.Get("YesNoPointer") as GameObject;
                if (pointer != null) pointer.transform.position = text.transform.position;
                return true;
            }
            return false;
        }
        internal static bool IsLegacyMouseAction()
        {
            GameObject target=Interact==null?null:Interact.Get("InteractingObject") as GameObject;
            foreach(View show in slideshows)if(show.Owner!=null && show.Owner.isActiveAndEnabled && (show.Flag("startedConvo")||target==show.Owner.gameObject))return true;
            foreach(View activation in extraMouse)if(activation.Owner!=null && activation.Owner.isActiveAndEnabled)return true;
            return false;
        }
        internal static bool TryExitInteraction()
        {
            if(Current!=InputContext.World || exitInteraction==null || exitInteraction.Owner==null)return false;
            // SecurityCameras owns a four-second transition guard; keep its authored exit events and fade.
            if(exitMethod!="ExitPeep" || exitInteraction.Get("co")==null)exitInteraction.Invoke(exitMethod);
            return true;
        }
        internal static KeyCode ChoiceKey(bool previous)
        {
            bool increment = previous ? !choiceDescending : choiceDescending;
            return increment ? KeyCode.W : KeyCode.S;
        }
    }
}
