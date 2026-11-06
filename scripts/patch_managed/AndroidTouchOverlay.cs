using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ClosingShift.Android
{
    public sealed class AndroidTouchOverlay : MonoBehaviour
    {
        readonly List<AndroidTouchRegion> regions = new List<AndroidTouchRegion>();
        readonly List<RectTransform> scalable = new List<RectTransform>();
        readonly List<Text> staticLabels = new List<Text>();
        readonly List<string> staticCaptions = new List<string>();
        readonly Vector3[] corners = new Vector3[4];
        readonly AndroidLookAcceleration lookAcceleration = new AndroidLookAcceleration();
        readonly AndroidThreeFingerTap crouchTap = new AndroidThreeFingerTap();
        internal static bool SuppressTouches { get; private set; }
        Vector2 moveOrigin, lookOrigin;
        float lookTravel;
        bool primaryHeld;
        float displayDensity, lookGestureAt, lookGesturePixels, lookGestureMaxGain;
        int nativeShortSide, reportedLookGestures;
        bool nativeLookMetrics;
        RectTransform safe, gameplay, dialogueRegion, inventoryRegion, lookRegion, moveRegion, primary, previous, next, bag, settings, settingsShield, settingsButton, focus, pointer, pauseRegion;
        RectTransform help, qaOrder;
        Text helpText;
        static float qaNoticeUntil;
        string hintCaption="";
        CanvasGroup pauseHint, bagHint, hintFade;
        float tutorialRemaining;
        bool tutorialWasWorld, tutorialStarted, tutorialEnded;
        int tutorialScene = -1;
        Text bagLabel, primaryCaption, previousLabel, nextLabel, hint, settingsValues, invertTouchLabel, invertPadLabel, leftHandedLabel;
        Font font;
        Rect lastSafe;
        bool built, lastRussian;
        int actionScene = -1, reportedActions;
        int lastContext=-1;
        internal static float MenuScale = 1;
        public static Transform SettingsScope { get; private set; }
        internal static bool SettingsOpen { get { return SettingsScope != null && SettingsScope.gameObject.activeSelf; } }
        internal static void ShowQaEnabled(){qaNoticeUntil=Time.unscaledTime+2.5f;}
        static string Local(string english, string russian) { return ClosingShift.Localization.RussianUiPresentation.RussianActive ? russian : english; }
        static string Caption(string source)
        {
            switch(source) {
                case "MOVE":return Local(source,"ХОДЬБА");case "RUN":return Local(source,"БЕГ");
                case "CROUCH":return Local(source,"ПРИСЕСТЬ");case "BAG":return Local(source,"ВЕЩИ");
                case "PAUSE / BACK":return Local(source,"ПАУЗА / НАЗАД");case "CONTROLS":return Local("TOUCH & GAMEPAD","СЕНСОР И ГЕЙМПАД");
                case "RESET":return Local(source,"СБРОС");case "CLOSE":return Local(source,"ЗАКРЫТЬ");
                default:return source;
            }
        }

        void Start()
        {
            CacheLookMetrics();
            MenuScale=Mathf.Clamp(PlayerPrefs.GetFloat("ClosingShift.MenuScale",1),.8f,1);
            try { font=Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch(ArgumentException) { }
            if(font==null) font=Font.CreateDynamicFontFromOSFont("sans-serif",20);
            if(font==null) font=Resources.Load<Font>("closing_shift/fonts/liberation_sans");
            if(font==null) font=Resources.Load<Font>("closing_shift/fonts/oswald_regular");
            if(font==null) throw new InvalidOperationException("Touch labels require the staged audited font aliases");
            var canvasObject=new GameObject("Android touch controls",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(AndroidOverlayRaycaster));
            canvasObject.transform.SetParent(transform,false);
            Canvas canvas=canvasObject.GetComponent<Canvas>(); canvas.renderMode=RenderMode.ScreenSpaceOverlay; canvas.sortingOrder=32700;
            CanvasScaler scaler=canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1280,720); scaler.matchWidthOrHeight=.5f;
            safe=Rect("Safe area",canvasObject.transform,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            gameplay=Rect("Gameplay",safe,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            dialogueRegion=Region("Dialogue gestures",safe,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero,new Color(0,0,0,.001f)).transform as RectTransform;
            ConfigureDialogue(dialogueRegion.GetComponent<AndroidTouchRegion>());
            lookRegion=Region("Look or pointer",gameplay,new Vector2(.35f,0),Vector2.one,Vector2.zero,Vector2.zero,new Color(0,0,0,.001f)).transform as RectTransform;
            var lookTouch=lookRegion.GetComponent<AndroidTouchRegion>();
            lookTouch.begin=e=>{lookOrigin=e.position;lookTravel=0;lookGestureAt=Time.unscaledTime;lookGesturePixels=0;lookGestureMaxGain=1;lookAcceleration.Begin(lookGestureAt);};
            lookTouch.drag=e=> {
                if(e.pointerId<0) return;
                lookTravel=Mathf.Max(lookTravel,(e.position-lookOrigin).sqrMagnitude);
                if(AndroidContext.CanLook) {
                    float pixels=e.delta.magnitude;
                    float gain=lookAcceleration.Sample(pixels,LookDensity(),Time.unscaledTime);
                    if(reportedLookGestures<3){lookGesturePixels+=pixels;lookGestureMaxGain=Mathf.Max(lookGestureMaxGain,gain);}
                    Vector2 delta=e.delta*(720f/Math.Max(1,Screen.height))*.06f*AndroidInputSettings.TouchSensitivity*gain;
                    if(AndroidInputSettings.InvertTouchY) delta.y=-delta.y;
                    AndroidInput.AddLookDelta(delta);
                } else AndroidInput.MovePointer(e.delta);
            };
            lookTouch.end=e=>{
                if(e!=null && AndroidContext.Current==InputContext.World &&
                    AndroidTouchPolicy.IsTap(Mathf.Max(lookTravel,(e.position-lookOrigin).sqrMagnitude),Screen.height*.035f,Time.unscaledTime-lookGestureAt) &&
                    lookOrigin.x>=Screen.width*.4f && lookOrigin.x<=Screen.width*.6f && lookOrigin.y>=Screen.height*.3f && lookOrigin.y<=Screen.height*.7f)
                    Pulse("Interact",KeyCode.Mouse0);
                if(reportedLookGestures>=3 || lookGesturePixels<=0)return;
                ++reportedLookGestures;
                Debug.Log("PORT_TOUCH_LOOK gesture="+reportedLookGestures+" duration="+(Time.unscaledTime-lookGestureAt)+
                    " pixels="+lookGesturePixels+" maxGain="+lookGestureMaxGain+" renderHeight="+Screen.height+" renderDensity="+LookDensity());
                lookGesturePixels=0;
            };
            lookTouch.cancel=e=>lookGesturePixels=0;
            var stick=Region("Move",gameplay,Vector2.zero,new Vector2(.45f,1),Vector2.zero,Vector2.zero,new Color(0,0,0,.001f));
            moveRegion=stick.transform as RectTransform;
            stick.begin=e=>{moveOrigin=e.position;Move(e);}; stick.drag=Move;
            stick.end=e=>ReleaseMove(); stick.cancel=e=>ReleaseMove();
            primary=KeyButton("USE / HOLD",.89f,.22f,100,"Interact",KeyCode.Mouse0);
            primary.GetComponent<Image>().enabled=false;
            Rect("Action icon",primary,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero).gameObject.AddComponent<AndroidActionIcon>();
            primaryCaption=AddLabel(Rect("Use caption",primary,new Vector2(.5f,0),new Vector2(.5f,0),new Vector2(0,-20),new Vector2(190,30)),"");
            primaryCaption.fontSize=16;
            previous=SelectionButton("PREVIOUS",.39f); next=SelectionButton("NEXT",.52f);
            previousLabel=previous.GetComponentInChildren<Text>(); nextLabel=next.GetComponentInChildren<Text>();
            inventoryRegion=Region("Inventory gestures",safe,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero,new Color(0,0,0,.001f)).transform as RectTransform;
            ConfigureInventory(inventoryRegion.GetComponent<AndroidTouchRegion>());
            inventoryRegion.SetAsFirstSibling();
            bag=EdgeRegion("BAG",new Vector2(.78f,.70f),Vector2.one,()=>{
                if(AndroidContext.Current!=InputContext.World && AndroidContext.Current!=InputContext.Inventory)return;
                SetText(bagLabel,Local("BACK","НАЗАД"));
                Pulse("Inventory",KeyCode.Tab);
            },out bagHint);
            bagLabel=bag.GetComponentInChildren<Text>();
            pauseRegion=EdgeRegion("PAUSE / BACK",new Vector2(0,.70f),new Vector2(.23f,1),()=>Pulse(null,KeyCode.Escape),out pauseHint);
            if(AndroidQaOrders.Available)
                qaOrder=NativeButton("QA",safe,new Vector2(.5f,.72f),Vector2.zero,new Vector2(150,64),AndroidQaOrders.CompleteSelected);
            BuildSettings();
            RectTransform hintRect=Rect("Context hint",safe,new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(0,-115),new Vector2(850,52));
            hint=AddLabel(hintRect,""); if(hint!=null) hint.fontSize=16;
            hintFade=hintRect.gameObject.AddComponent<CanvasGroup>();hintFade.blocksRaycasts=false;
            focus=Rect("Gamepad focus",safe,Vector2.zero,Vector2.zero,Vector2.zero,Vector2.zero);
            Image focusImage=focus.gameObject.AddComponent<Image>(); focusImage.color=new Color(1,.8f,.15f,.1f); focusImage.raycastTarget=false;
            Outline outline=focus.gameObject.AddComponent<Outline>(); outline.effectColor=new Color(1,.8f,.15f,1); outline.effectDistance=new Vector2(3,3);
            pointer=Rect("Virtual pointer",safe,Vector2.zero,Vector2.zero,Vector2.zero,new Vector2(18,18));
            Image pointerImage=pointer.gameObject.AddComponent<Image>(); pointerImage.color=Color.yellow; pointerImage.raycastTarget=false;
            if(EventSystem.current==null) new GameObject("Android EventSystem",typeof(EventSystem),typeof(StandaloneInputModule)).transform.SetParent(transform,false);
            built=true; gameplay.gameObject.SetActive(false); ScaleControls(); ApplyCap();
            ClosingShift.Localization.RussianLocaleDefaults.AfterOverlayCreated(canvasObject);
            AndroidInput.RefreshUi();
        }
        void CacheLookMetrics()
        {
            try
            {
                using(var player=new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using(var activity=player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if(activity==null)throw new InvalidOperationException("Android activity is unavailable");
                    using(var resources=activity.Call<AndroidJavaObject>("getResources"))
                    {
                        if(resources==null)throw new InvalidOperationException("Android resources are unavailable");
                        using(var metrics=resources.Call<AndroidJavaObject>("getDisplayMetrics"))
                        {
                            if(metrics==null)throw new InvalidOperationException("Android display metrics are unavailable");
                            displayDensity=metrics.Get<float>("density");
                            Resolution displaySize=MobileRenderSettings.GetNativeResolution();
                            nativeShortSide=Math.Min(displaySize.width,displaySize.height);
                            if(nativeShortSide<2) nativeShortSide=Math.Min(metrics.Get<int>("widthPixels"),metrics.Get<int>("heightPixels"));
                            if(AndroidTouchPolicy.LookDensity(displayDensity,Screen.height,nativeShortSide)<=0)
                                throw new InvalidOperationException("Invalid Android display metrics");
                            nativeLookMetrics=true;
                        }
                    }
                }
            }
            catch(Exception error) when(error is AndroidJavaException || error is InvalidOperationException)
            { Debug.LogWarning("PORT_TOUCH_METRICS fallback="+error.Message); }
            Debug.Log("PORT_TOUCH_METRICS source="+(nativeLookMetrics?"AndroidDisplayMetrics":"UnityFallback")+
                " logicalDensity="+displayDensity+" nativeShortSide="+nativeShortSide+" render="+Screen.width+"x"+Screen.height+
                " renderDensity="+LookDensity()+" screenDpi="+Screen.dpi);
        }
        float LookDensity()
        {
            // Unity currentResolution may report the reduced render surface rather than native display pixels.
            return nativeLookMetrics?AndroidTouchPolicy.LookDensity(displayDensity,Screen.height,nativeShortSide):
                Mathf.Max(.5f,Screen.dpi/160f)*Screen.height/Mathf.Max(Screen.height,Screen.currentResolution.height);
        }
        static void Pulse(string action,KeyCode fallback)
        { KeyCode key=action==null?fallback:AndroidInput.KeyFor(action,fallback);AndroidInput.SetKey(key,true);AndroidInput.SetKey(key,false); }
        RectTransform EdgeRegion(string title,Vector2 min,Vector2 max,Action tap,out CanvasGroup hintGroup)
        {
            var region=Region(title,safe.parent,min,max,Vector2.zero,Vector2.zero,new Color(0,0,0,.001f));
            RectTransform rect=region.transform as RectTransform;
            RectTransform visual=Rect("Edge tutorial",rect,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            hintGroup=visual.gameObject.AddComponent<CanvasGroup>();hintGroup.blocksRaycasts=false;
            var outline=visual.gameObject.AddComponent<AndroidDashedOutline>();outline.color=new Color(1,1,1,.75f);outline.raycastTarget=false;
            AddLabel(visual,title);
            Vector2 start=Vector2.zero;float at=0,travel=0;
            region.begin=e=>{start=e.position;at=Time.unscaledTime;travel=0;};
            region.drag=e=>travel=Mathf.Max(travel,(e.position-start).sqrMagnitude);
            region.end=e=>{
                if(e==null)return;
                travel=Mathf.Max(travel,(e.position-start).sqrMagnitude);
                if(!AndroidTouchPolicy.IsTap(travel,Screen.height*.035f,Time.unscaledTime-at))return;
                tap();
            };
            region.cancel=e=>travel=Single.PositiveInfinity;
            return rect;
        }
        void ConfigureInventory(AndroidTouchRegion region)
        {
            Vector2 start=Vector2.zero;float at=0,travel=0;
            region.begin=e=>{start=e.position;at=Time.unscaledTime;travel=0;};
            region.drag=e=>travel=Mathf.Max(travel,(e.position-start).sqrMagnitude);
            region.end=e=>{
                if(e==null)return;
                Vector2 delta=e.position-start;
                int gesture=AndroidTouchPolicy.InventoryGesture(delta.x,delta.y,Mathf.Max(travel,delta.sqrMagnitude),Screen.width,Screen.height,Time.unscaledTime-at,start.x/Screen.width);
                if(gesture==2)Pulse("Interact",KeyCode.Mouse0);
                else if(gesture!=0)Pulse(gesture<0?"Left":"Right",gesture<0?KeyCode.A:KeyCode.D);
            };
            region.cancel=e=>travel=Single.PositiveInfinity;
        }
        void ConfigureDialogue(AndroidTouchRegion region)
        {
            Vector2 start=Vector2.zero;float at=0,travel=0;bool overChoice=false;
            region.begin=e=>{start=e.position;at=Time.unscaledTime;travel=0;overChoice=AndroidContext.HoverChoice(e.position);};
            region.drag=e=>{travel=Mathf.Max(travel,(e.position-start).sqrMagnitude);overChoice=AndroidContext.HoverChoice(e.position);};
            region.end=e=>{
                if(e==null)return;
                travel=Mathf.Max(travel,(e.position-start).sqrMagnitude);
                if(!AndroidTouchPolicy.IsTap(travel,Screen.height*.035f,Time.unscaledTime-at))return;
                if(AndroidContext.Current==InputContext.Choice && !overChoice && AndroidContext.ChoiceFocus==null)return;
                KeyCode key=AndroidInput.KeyFor("Interact",KeyCode.Mouse0);AndroidInput.SetKey(key,true);AndroidInput.SetKey(key,false);
            };
            region.cancel=e=>{travel=Single.PositiveInfinity;};
        }
        void ReleaseMove() { AndroidInput.SetMove(Vector2.zero,false);AndroidInput.CancelKey(AndroidInput.KeyFor("Run",KeyCode.LeftShift)); }
        void Move(PointerEventData e)
        {
            if(e==null || e.pointerId<0) return;
            Vector2 delta=e.position-moveOrigin;
            float radius=AndroidTouchPolicy.MoveRadius(Screen.height,AndroidInputSettings.Scale);
            Vector2 direction=delta.magnitude<radius*.08f?Vector2.zero:Vector2.ClampMagnitude(delta/radius,1);
            AndroidInput.SetMove(direction,true);
            AndroidInput.SetKey(AndroidInput.KeyFor("Run",KeyCode.LeftShift),AndroidTouchPolicy.ShouldRun(delta.magnitude,radius));
        }
        RectTransform KeyButton(string title,float x,float y,float size,string action,KeyCode fallback)
        {
            var region=Region(title,gameplay,new Vector2(x,y),new Vector2(x,y),Vector2.zero,new Vector2(size,size),new Color(0,0,0,.22f));
            RectTransform rect=region.transform as RectTransform; scalable.Add(rect); Text label=action=="Interact"?null:AddLabel(rect,title);
            if(label!=null && (title=="CROUCH" || title=="BAG")){label.resizeTextForBestFit=true;label.resizeTextMinSize=14;label.resizeTextMaxSize=label.fontSize;label.horizontalOverflow=HorizontalWrapMode.Overflow;}
            KeyCode pressed=KeyCode.None;
            region.begin=e=>{
                if(action=="Interact" && AndroidContext.TryExitInteraction())return;
                if(action=="Interact")primaryHeld=true;
                pressed=action=="Interact" && AndroidContext.Current==InputContext.Document ? KeyCode.Escape : AndroidInput.KeyFor(action,fallback);
                int scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                if(actionScene!=scene){actionScene=scene;reportedActions=0;}
                if(action=="Interact" && reportedActions++<8 && Camera.main!=null) {
                    GameObject target=AndroidContext.Interact==null?null:AndroidContext.Interact.Get("InteractingObject") as GameObject;
                    Debug.Log("PORT_USE context="+AndroidContext.Current+" key="+pressed+" target="+(target==null?"none":target.name)+" position="+Camera.main.transform.position+" rotation="+Camera.main.transform.eulerAngles);
                }
                AndroidInput.SetKey(pressed,true);
            };
            region.end=e=>{AndroidInput.SetKey(pressed,false);pressed=KeyCode.None;if(action=="Interact")primaryHeld=false;};
            region.cancel=e=>{AndroidInput.CancelKey(pressed);pressed=KeyCode.None;if(action=="Interact")primaryHeld=false;};
            return rect;
        }
        RectTransform SelectionButton(string name,float x)
        {
            var region=Region(name,gameplay,new Vector2(x,.17f),new Vector2(x,.17f),Vector2.zero,new Vector2(110,72),new Color(0,0,0,.5f));
            RectTransform rect=region.transform as RectTransform; scalable.Add(rect); AddLabel(rect,name);
            bool previousButton=name=="PREVIOUS"; KeyCode pressed=KeyCode.None;
            region.begin=e=>{
                if(AndroidContext.Current==InputContext.Choice) pressed=AndroidContext.ChoiceKey(previousButton);
                else if(AndroidContext.Current==InputContext.Document || AndroidContext.Current==InputContext.Inventory) pressed=AndroidInput.KeyFor(previousButton?"Left":"Right",previousButton?KeyCode.A:KeyCode.D);
                else {AndroidInput.AddScroll(previousButton?1:-1);return;}
                AndroidInput.SetKey(pressed,true);
            };
            region.end=e=>{AndroidInput.SetKey(pressed,false);pressed=KeyCode.None;};
            region.cancel=e=>{AndroidInput.CancelKey(pressed);pressed=KeyCode.None;};
            return rect;
        }
        void BuildSettings()
        {
            settingsShield=Rect("Control settings modal",safe,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            Image shield=settingsShield.gameObject.AddComponent<Image>();shield.color=new Color(0,0,0,.65f);
            settings=Rect("Control settings",settingsShield,new Vector2(.5f,.5f),new Vector2(.5f,.5f),Vector2.zero,new Vector2(660,650));
            Image background=settings.gameObject.AddComponent<Image>(); background.color=new Color(.04f,.05f,.07f,.98f);
            SettingsScope=settingsShield;
            RectTransform values=Rect("Settings values",settings,new Vector2(.5f,.5f),new Vector2(.5f,.5f),new Vector2(-60,82),new Vector2(420,320));
            settingsValues=AddLabel(values,""); if(settingsValues!=null) {settingsValues.fontSize=28;settingsValues.alignment=TextAnchor.MiddleLeft;settingsValues.horizontalOverflow=HorizontalWrapMode.Overflow;}
            for(int row=0;row<5;++row) {
                int capture=row; float y=212-row*65;
                NativeButton("-",settings,new Vector2(.5f,.5f),new Vector2(165,y),new Vector2(58,52),()=>Adjust(capture,-1));
                NativeButton("+",settings,new Vector2(.5f,.5f),new Vector2(235,y),new Vector2(58,52),()=>Adjust(capture,1));
            }
            invertTouchLabel=NativeButton("Invert touch Y",settings,new Vector2(.5f,.5f),new Vector2(-150,-140),new Vector2(275,54),()=>{AndroidInputSettings.InvertTouchY=!AndroidInputSettings.InvertTouchY;SettingsChanged();}).GetComponentInChildren<Text>();
            invertPadLabel=NativeButton("Invert pad Y",settings,new Vector2(.5f,.5f),new Vector2(150,-140),new Vector2(275,54),()=>{AndroidInputSettings.InvertPadY=!AndroidInputSettings.InvertPadY;SettingsChanged();}).GetComponentInChildren<Text>();
            leftHandedLabel=NativeButton("Left-handed controls",settings,new Vector2(.5f,.5f),new Vector2(0,-202),new Vector2(575,54),()=>{AndroidInputSettings.LeftHanded=!AndroidInputSettings.LeftHanded;SettingsChanged();}).GetComponentInChildren<Text>();
            NativeButton("?",settings,new Vector2(.5f,.5f),new Vector2(270,283),new Vector2(58,52),()=>ShowHelp(true));
            NativeButton("RESET",settings,new Vector2(.5f,.5f),new Vector2(-150,-274),new Vector2(275,60),()=>{
                AndroidInputSettings.TouchSensitivity=1;AndroidInputSettings.PadSensitivity=8;AndroidInputSettings.MoveDeadzone=AndroidInputSettings.LookDeadzone=.19f;AndroidInputSettings.Scale=1;
                AndroidInputSettings.InvertTouchY=AndroidInputSettings.InvertPadY=AndroidInputSettings.LeftHanded=false;MenuScale=1; SettingsChanged();
            });
            NativeButton("CLOSE",settings,new Vector2(.5f,.5f),new Vector2(150,-274),new Vector2(275,60),()=>ShowSettings(false));
            foreach(Text text in settings.GetComponentsInChildren<Text>(true))
                if(text!=settingsValues){text.fontSize=25;text.resizeTextForBestFit=true;text.resizeTextMinSize=22;text.resizeTextMaxSize=25;}
            help=Rect("Touch help",settingsShield,new Vector2(.5f,.5f),new Vector2(.5f,.5f),Vector2.zero,new Vector2(660,650));
            Image helpBackground=help.gameObject.AddComponent<Image>();helpBackground.color=background.color;
            helpText=AddLabel(Rect("Help text",help,new Vector2(.5f,.5f),new Vector2(.5f,.5f),new Vector2(0,30),new Vector2(575,490)),"");
            helpText.fontSize=23;helpText.alignment=TextAnchor.MiddleLeft;helpText.resizeTextForBestFit=true;helpText.resizeTextMinSize=18;helpText.resizeTextMaxSize=23;
            NativeButton("CLOSE",help,new Vector2(.5f,.5f),new Vector2(0,-274),new Vector2(275,60),()=>ShowHelp(false));
            help.gameObject.SetActive(false);
            settingsShield.gameObject.SetActive(false);
        }
        void Adjust(int row,int sign)
        {
            if(row==0) AndroidInputSettings.TouchSensitivity=Mathf.Clamp(AndroidInputSettings.TouchSensitivity+sign*.1f,.25f,3);
            if(row==1) AndroidInputSettings.PadSensitivity=Mathf.Clamp(AndroidInputSettings.PadSensitivity+sign,1,24);
            if(row==2) AndroidInputSettings.MoveDeadzone=AndroidInputSettings.LookDeadzone=Mathf.Clamp(AndroidInputSettings.MoveDeadzone+sign*.025f,.05f,.4f);
            if(row==3) AndroidInputSettings.Scale=Mathf.Clamp(AndroidInputSettings.Scale+sign*.05f,.7f,1.4f);
            if(row==4) MenuScale=Mathf.Clamp(MenuScale+sign*.05f,.8f,1);
            SettingsChanged();
        }
        void SettingsChanged(){ PlayerPrefs.SetFloat("ClosingShift.MenuScale",MenuScale);AndroidInputSettings.Save();ScaleControls();UpdateSettingsLabels(); }
        void UpdateSettingsLabels()
        {
            if(settingsValues==null) return;
            SetText(settingsValues,Local("Touch look","Обзор касанием")+": "+AndroidInputSettings.TouchSensitivity.ToString("0.00")+"\n\n"+
                Local("Pad look","Обзор стиком")+": "+AndroidInputSettings.PadSensitivity.ToString("0.0")+"\n\n"+
                Local("Stick dead zone","Мёртвая зона")+": "+AndroidInputSettings.MoveDeadzone.ToString("0.00")+"\n\n"+
                Local("Touch size","Размер кнопок")+": "+AndroidInputSettings.Scale.ToString("0.00")+"\n\n"+
                Local("Menu size","Размер меню")+": "+MenuScale.ToString("0.00"));
            SetText(invertTouchLabel,Local("Invert touch Y","Инверсия касания Y")+": "+(AndroidInputSettings.InvertTouchY?Local("On","Вкл."):Local("Off","Выкл.")));
            SetText(invertPadLabel,Local("Invert pad Y","Инверсия стика Y")+": "+(AndroidInputSettings.InvertPadY?Local("On","Вкл."):Local("Off","Выкл.")));
            SetText(leftHandedLabel,Local("Left-handed controls","Управление для левши")+": "+(AndroidInputSettings.LeftHanded?Local("On","Вкл."):Local("Off","Выкл.")));
        }
        void ScaleControls()
        {
            foreach(RectTransform rect in scalable) rect.localScale=Vector3.one*AndroidInputSettings.Scale;
            if(moveRegion==null || lookRegion==null)return;
            moveRegion.anchorMin=new Vector2(AndroidInputSettings.LeftHanded?.55f:0,0);
            moveRegion.anchorMax=new Vector2(AndroidInputSettings.LeftHanded?1:.45f,1);
            lookRegion.anchorMin=new Vector2(AndroidInputSettings.LeftHanded?0:.45f,0);
            lookRegion.anchorMax=new Vector2(AndroidInputSettings.LeftHanded?.55f:1,1);
            primary.anchorMin=primary.anchorMax=new Vector2(AndroidInputSettings.LeftHanded?.11f:.89f,.22f);
        }
        internal void ShowSettings(bool show)
        {
            if(settings==null) return;
            if(show){settings.gameObject.SetActive(true);help.gameObject.SetActive(false);}
            settingsShield.gameObject.SetActive(show); ReleaseAll(); AndroidInput.ContextChanged();
            UpdateSettingsLabels(); AndroidInput.RefreshUi();
            if(show && EventSystem.current!=null) {
                Button first=settings.GetComponentInChildren<Button>(); if(first!=null) EventSystem.current.SetSelectedGameObject(first.gameObject);
            }
        }
        void ShowHelp(bool show)
        {
            ReleaseAll();settings.gameObject.SetActive(!show);help.gameObject.SetActive(show);
            if(show)SetText(helpText,Local(
                "Drag on the left to walk; drag farther to run.\n\nDrag on the right to look. Tap the centre or the hand to use; hold the hand for continuous actions.\n\nTap with three fingers to crouch or stand.\n\nUpper right: inventory. Swipe to select an item; tap USE to equip it. Upper left or Android Back: pause / return.\n\nGamepad: A Use, X Crouch, Y Inventory, L2 Run, Start / B Back.",
                "Ведите слева для ходьбы; дальше от начала касания — бег.\n\nВедите справа для обзора. Тап в центре или по руке — действие; держите руку для длительных действий.\n\nТап тремя пальцами — присесть или встать.\n\nСправа сверху — вещи. Свайп выбирает предмет, ИСПОЛЬЗОВАТЬ берёт его в руку. Слева сверху или системная Назад — пауза / возврат.\n\nГеймпад: A действие, X присесть, Y вещи, L2 бег, Start / B назад."));
            AndroidInput.RefreshUi();
        }
        internal void CloseSettings(){if(help.gameObject.activeSelf)ShowHelp(false);else ShowSettings(false);}
        RectTransform NativeButton(string title,Transform parent,Vector2 anchor,Vector2 position,Vector2 size,Action callback)
        {
            RectTransform rect=Rect(title,parent,anchor,anchor,position,size);
            Image image=rect.gameObject.AddComponent<Image>();image.color=new Color(.15f,.18f,.23f,.98f);
            Button button=rect.gameObject.AddComponent<Button>(); button.targetGraphic=image;button.onClick.AddListener(()=>{AndroidInputSettings.Haptic();callback();});
            ColorBlock colors=button.colors;colors.highlightedColor=new Color(.9f,.8f,.45f);colors.selectedColor=colors.highlightedColor;button.colors=colors;
            AddLabel(rect,title);return rect;
        }
        static RectTransform Rect(string name,Transform parent,Vector2 min,Vector2 max,Vector2 position,Vector2 size)
        {
            var child=new GameObject(name,typeof(RectTransform));RectTransform rect=child.GetComponent<RectTransform>();rect.SetParent(parent,false);
            rect.anchorMin=min;rect.anchorMax=max;rect.anchoredPosition=position;rect.sizeDelta=size;return rect;
        }
        AndroidTouchRegion Region(string title,Transform parent,Vector2 min,Vector2 max,Vector2 position,Vector2 size,Color color)
        {
            RectTransform rect=Rect(title,parent,min,max,position,size);Image image=rect.gameObject.AddComponent<Image>();image.color=color;
            var region=rect.gameObject.AddComponent<AndroidTouchRegion>();regions.Add(region);return region;
        }
        Text AddLabel(RectTransform rect,string title)
        {
            RectTransform label=Rect("Label",rect,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            Text text=label.gameObject.AddComponent<Text>();text.font=font;text.fontSize=18;text.color=Color.white;
            text.alignment=TextAnchor.MiddleCenter;text.raycastTarget=false;text.text=title;
            if(!String.IsNullOrEmpty(title) && title!="FPS"){staticLabels.Add(text);staticCaptions.Add(title);}
            return text;
        }
        static void SetText(Text text,string value)
        {
            if(text==null || text.text==value) return;
            text.text=value;ClosingShift.Localization.RussianLocaleDefaults.AfterObjectLocalized(text.gameObject);
        }
        void LateUpdate()
        {
            if(!built) return;
            // InventoryOpen runs after the early input snapshot; render its new caption in this frame.
            AndroidContext.Update();
            InstallSettingsButton();
            if(SettingsOpen && !AndroidContext.NativeUI)ShowSettings(false);
            if(lastSafe!=Screen.safeArea){lastSafe=Screen.safeArea;safe.anchorMin=new Vector2(lastSafe.xMin/Screen.width,lastSafe.yMin/Screen.height);safe.anchorMax=new Vector2(lastSafe.xMax/Screen.width,lastSafe.yMax/Screen.height);}
            bool dialogue=AndroidContext.Current==InputContext.Dialogue || AndroidContext.Current==InputContext.Choice;
            bool inventory=AndroidContext.Current==InputContext.Inventory;
            bool show=!AndroidContext.NativeUI && AndroidContext.Current!=InputContext.Rebind && !dialogue;
            if(gameplay.gameObject.activeSelf!=show) gameplay.gameObject.SetActive(show);
            dialogueRegion.gameObject.SetActive(dialogue);
            int scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if(tutorialScene!=scene){tutorialScene=scene;tutorialRemaining=5;tutorialWasWorld=tutorialStarted=tutorialEnded=false;}
            bool introPending=AndroidContext.IntroPending;
            bool controllableWorld=AndroidContext.Current==InputContext.World && AndroidContext.CanMove && !introPending;
            // HFPS enables movement before the authored opening conversation starts.
            tutorialRemaining=AndroidTouchPolicy.TutorialRemaining(tutorialRemaining,controllableWorld && tutorialWasWorld,Time.unscaledDeltaTime);
            tutorialWasWorld=controllableWorld;
            bool tutorial=controllableWorld && tutorialRemaining>0;
            float tutorialAlpha=tutorial?AndroidTouchPolicy.TutorialAlpha(tutorialRemaining):0;
            pauseHint.alpha=tutorialAlpha;bagHint.alpha=inventory?1:tutorialAlpha;
            bool qaNotice=Time.unscaledTime<qaNoticeUntil;
            hintFade.alpha=qaNotice?Mathf.Clamp01((qaNoticeUntil-Time.unscaledTime)/.6f):AndroidContext.Current==InputContext.Rebind?1:tutorialAlpha;
            inventoryRegion.gameObject.SetActive(inventory);
            if(qaOrder!=null)qaOrder.gameObject.SetActive(inventory && AndroidQaOrders.HasSelectedOrder);
            float fit=Mathf.Min(1,Mathf.Min(safe.rect.width*.94f/660,safe.rect.height*.92f/650));
            Vector3 settingsScale=Vector3.one*fit;if(settings.localScale!=settingsScale)settings.localScale=settingsScale;
            if(help.localScale!=settingsScale)help.localScale=settingsScale;
            bool pointerWorld=AndroidContext.Current==InputContext.World && Cursor.lockState==CursorLockMode.None;
            lookRegion.gameObject.SetActive(show && (AndroidContext.CanLook || pointerWorld));
            moveRegion.gameObject.SetActive(show && AndroidContext.CanMove);
            primary.gameObject.SetActive(show && (inventory || AndroidContext.Current==InputContext.Document || AndroidContext.CanInteract || primaryHeld));
            bag.gameObject.SetActive(show && (AndroidContext.Current==InputContext.World || AndroidContext.Current==InputContext.Inventory));
            bool selection=AndroidContext.Current==InputContext.Document;
            object page=AndroidContext.Reading==null?null:AndroidContext.Reading.Get("curPageNum");
            object lastPage=AndroidContext.Reading==null?null:AndroidContext.Reading.Get("maxPageNum");
            previous.gameObject.SetActive(show && page is int && lastPage is int && AndroidTouchPolicy.DocumentPage(selection,(int)page,(int)lastPage,true));
            next.gameObject.SetActive(show && page is int && lastPage is int && AndroidTouchPolicy.DocumentPage(selection,(int)page,(int)lastPage,false));
            pauseRegion.gameObject.SetActive(!inventory && !dialogue && (AndroidContext.CanPause || SettingsOpen));
            if(tutorial && !tutorialStarted){tutorialStarted=true;ReportTutorial("start",introPending);}
            if(tutorialStarted && !tutorialEnded && tutorialRemaining<=0){tutorialEnded=true;ReportTutorial("end",introPending);}
            if(hint!=null){hint.gameObject.SetActive(qaNotice || AndroidContext.Current==InputContext.Rebind || (AndroidContext.Current==InputContext.World && tutorial));hint.fontSize=qaNotice?23:16;}
            bool russian=ClosingShift.Localization.RussianUiPresentation.RussianActive;
            int mode=(int)AndroidContext.Current*10+(AndroidContext.CanMove?1:0)+(AndroidContext.CanLook?2:0)+(pointerWorld?4:0)+(AndroidInput.LastWasPad?1000:0);
            if(lastContext!=mode || lastRussian!=russian){
                lastContext=mode;lastRussian=russian;
                // The authored inventory title and description occupy the bottom centre.
                previous.anchorMin=previous.anchorMax=inventory?new Vector2(.28f,.38f):new Vector2(.39f,.17f);
                next.anchorMin=next.anchorMax=inventory?new Vector2(.72f,.38f):new Vector2(.52f,.17f);
                for(int i=0;i<staticLabels.Count;++i)SetText(staticLabels[i],Caption(staticCaptions[i]));
                SetText(previousLabel,Local("PREVIOUS","НАЗАД"));SetText(nextLabel,Local("NEXT","ВПЕРЁД"));
                hintCaption=AndroidContext.Current==InputContext.Rebind?Local("Keyboard binding: press a physical key, or use BACK to cancel","Назначение клавиши: физическая клавиатура или НАЗАД для отмены"):AndroidInput.LastWasPad?Local("Android pad: A Use   X Crouch   Y Bag   L2 Run   Start / B Back","Геймпад Android: A Действие   X Присесть   Y Инвентарь   L2 Бег   Start / B Назад"):Local("Drag to move; farther to run  |  Centre tap: use  |  Tap with 3 fingers: crouch","Движение от касания; дальше — бег  |  Тап в центре: действие  |  Тап тремя пальцами: присесть");
                UpdateSettingsLabels();
            }
            SetText(hint,qaNotice?Local("QA enabled","QA включён"):hintCaption);
            SetText(bagLabel,inventory?Local("BACK","НАЗАД"):Caption("BAG"));
            SetText(primaryCaption,inventory?Local("USE","ИСПОЛЬЗОВАТЬ"):"");
            RectTransform target=AndroidUiInput.FocusRect;
            focus.gameObject.SetActive(target!=null && AndroidInput.LastWasPad);
            if(target!=null){target.GetWorldCorners(corners);Vector2 local;RectTransformUtility.ScreenPointToLocalPointInRectangle(safe,(corners[0]+corners[2])*.5f,null,out local);focus.anchoredPosition=local+safe.rect.size*.5f;focus.sizeDelta=new Vector2(corners[2].x-corners[0].x,corners[2].y-corners[0].y)/safe.lossyScale.x+new Vector2(10,10);}
            pointer.gameObject.SetActive(AndroidInput.PointerActive && !dialogue);
            if(AndroidInput.PointerActive){Vector2 local;RectTransformUtility.ScreenPointToLocalPointInRectangle(safe,AndroidInput.PointerPosition,null,out local);pointer.anchoredPosition=local+safe.rect.size*.5f;}
            ApplyCap();
        }
        void ReportTutorial(string phase,bool introPending)
        {
            Debug.Log("PORT_TOUCH hints="+phase+" scene="+UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex+
                " context="+AndroidContext.Current+" introPending="+introPending+" remaining="+tutorialRemaining+
                " bagActive="+bag.gameObject.activeInHierarchy+" pauseActive="+pauseRegion.gameObject.activeInHierarchy+" alpha="+pauseHint.alpha);
        }
        void ApplyCap()
        {
            int cap=AndroidTouchPolicy.FrameCap(AndroidInputSettings.FpsValues[AndroidInputSettings.FpsMode],AndroidInputSettings.DisplayRate,AndroidContext.NativeUI);
            AndroidInputSettings.RequestFrameRate(cap);
            if(Application.targetFrameRate!=cap) Application.targetFrameRate=cap;
            if(QualitySettings.vSyncCount!=0) QualitySettings.vSyncCount=0;
        }
        void InstallSettingsButton()
        {
            if(settingsButton!=null || AndroidUiInput.OptionsPanel==null)return;
            Transform tab=AndroidUiInput.OptionsPanel.Find("Background/TABS/TAB_CONTROLS");
            RectTransform reset=tab==null?null:tab.Find("Default") as RectTransform;
            if(reset==null)return;
            settingsButton=NativeButton("CONTROLS",tab,reset.anchorMin,reset.anchoredPosition+new Vector2(-270,22.5f),new Vector2(250,44),()=>ShowSettings(true));
            SetText(settingsButton.GetComponentInChildren<Text>(),Caption("CONTROLS"));
            AndroidInput.RefreshUi();
        }
        internal void ProcessCapturedTouches()
        {
            bool fire=false;
            if(AndroidContext.Current==InputContext.World && AndroidContext.CanMove)
            {
                for(int i=0;i<Input.touchCount;++i)
                {
                    Touch touch=Input.GetTouch(i);
                    fire|=crouchTap.Sample(touch.fingerId,touch.position.x,touch.position.y,Time.unscaledTime,(int)touch.phase,Screen.height*.035f);
                }
                if(Input.touchCount==0 && crouchTap.HasTouches)crouchTap.Cancel();
            }
            else crouchTap.Cancel();
            if(crouchTap.Captured && !SuppressTouches)
            {
                foreach(AndroidTouchRegion region in regions)if(region!=null)region.Release(true);
                ReleaseMove();
            }
            SuppressTouches=crouchTap.Captured;
            if(fire)Pulse("Crouch",KeyCode.C);
            if(Cursor.lockState!=CursorLockMode.Locked)return;
            foreach(AndroidTouchRegion region in regions)if(region!=null)region.ProcessCapturedTouch();
        }
        public void ReleaseAll(){crouchTap.Cancel();SuppressTouches=false;foreach(AndroidTouchRegion region in regions)if(region!=null)region.Release(true);ReleaseMove();}
        void OnDisable(){ReleaseAll();}
    }
    public sealed class AndroidOverlayRaycaster : GraphicRaycaster
    {
        public override void Raycast(PointerEventData data,List<RaycastResult> results)
        {if(data.pointerId<0 && !AndroidContext.NativeUI)return;base.Raycast(data,results);}
    }
}
