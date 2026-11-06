using ClosingShift.Android;
using UnityEngine;
using UnityEngine.EventSystems;

int assertions=0;
void Check(bool result,string name){++assertions;if(!result)throw new Exception(name);}
var icon=new AndroidActionIcon();var mesh=new UnityEngine.UI.VertexHelper();
typeof(AndroidActionIcon).GetMethod("OnPopulateMesh",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(icon,new object[]{mesh});
bool meshValid=mesh.vertices.Count<500&&mesh.triangles.Count%3==0;
foreach(var vertex in mesh.vertices)meshValid&=float.IsFinite(vertex.x)&&float.IsFinite(vertex.y)&&vertex.magnitude<=50.001f;
for(int i=0;i<mesh.triangles.Count;i+=3){var a=mesh.vertices[mesh.triangles[i]];var b=mesh.vertices[mesh.triangles[i+1]];var c=mesh.vertices[mesh.triangles[i+2]];meshValid&=(b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x)>=-.001f;}
Check(meshValid,"Hand and ring contain finite, consistently wound triangles inside the circular button without a concave triangle fan");
Check(icon.Raycast(new Vector2(0,0),null)&&!icon.Raycast(new Vector2(40,40),null),"Hand icon keeps a circular accessible hit area");
Check(AndroidTouchPolicy.IsKeyboardTutorial("Canvas (Options menu)/Show tutorial/tutorial",true),"Exact obsolete first-play keyboard artwork is hidden");
Check(AndroidTouchPolicy.IsKeyboardTutorial("Canvas (Options menu)/UI/PauseUI/tutorial",true),"Exact obsolete pause keyboard artwork is hidden");
Check(!AndroidTouchPolicy.IsKeyboardTutorial("Canvas (Options menu)/Show tutorial/tutorial",false),"Unrecognized tutorial structure remains visible");
Check(!AndroidTouchPolicy.IsKeyboardTutorial("Canvas (Options menu)/Naomi Intro Panel",true) && !AndroidTouchPolicy.IsKeyboardTutorial("Canvas (Options menu)/Show tutorial",true),"Story panel and authored animation parent remain visible");
Check(AndroidTouchPolicy.AccelerationGain(.2f)==1 && AndroidTouchPolicy.AccelerationGain(1.5f)==1.75f && AndroidTouchPolicy.AccelerationGain(9)==2.5f,"Borderlands2 slow, intermediate and fast look curve");
Check(AndroidTouchPolicy.AccelerationGain(float.NaN)==1 && AndroidTouchPolicy.AccelerationGain(float.PositiveInfinity)==1,"Invalid look speed stays linear");
var accel=new AndroidLookAcceleration();accel.Begin(1);float accelerated=accel.Sample(200,2,1.016f);
Check(accelerated>2 && accelerated<=2.5f,"Smoothed fast swipe accelerates with bounded gain");
accel.Begin(2);Check(accel.Sample(1,2,2.02f)==1,"Fresh touch clears the previous fast swipe");
float density360=AndroidTouchPolicy.LookDensity(2.75f,360,1080),density540=AndroidTouchPolicy.LookDensity(2.75f,540,1080);
Check(Math.Abs((700f*360/1080)/density360-700/2.75f)<1e-4f && Math.Abs((700f*540/1080)/density540-700/2.75f)<1e-4f,"Native Android logical density maps the same physical swipe to the same dp at360 and540");
Check(AndroidTouchPolicy.LookDensity(float.NaN,360,1080)==0 && AndroidTouchPolicy.LookDensity(float.PositiveInfinity,360,1080)==0 && AndroidTouchPolicy.LookDensity(0,360,1080)==0 && AndroidTouchPolicy.LookDensity(2.75f,360,0)==0 && AndroidTouchPolicy.LookDensity(2.75f,0,1080)==0 && AndroidTouchPolicy.LookDensity(float.MaxValue,360,1)==0,"Invalid Android metrics trigger explicit fallback instead of invalid speed");
float SwipeGain(int renderHeight,float seconds){var swipe=new AndroidLookAcceleration();swipe.Begin(0);float gain=1;for(int sample=1;sample<=10;++sample)gain=swipe.Sample(70f*renderHeight/1080,AndroidTouchPolicy.LookDensity(2.75f,renderHeight,1080),sample*seconds/10);return gain;}
float fast360=SwipeGain(360,.12f),fast540=SwipeGain(540,.12f);
Check(SwipeGain(360,1.2f)==1 && SwipeGain(540,1.2f)==1 && fast360>2 && fast360<=2.5f,"Equal700px slow and fast physical swipes remain distinguishable without changing base sensitivity");
Check(Math.Abs(fast360-fast540)<1e-5f,"Fast-look acceleration gain stays unchanged when the render profile changes");
Check(AndroidTouchPolicy.IsTap(4,12,.2f) && !AndroidTouchPolicy.IsTap(145,12,.2f) && !AndroidTouchPolicy.IsTap(0,12,.5f),"Edge and dialogue taps reject drags and long holds");
float tutorialRemaining=AndroidTouchPolicy.TutorialRemaining(5,true,.1f);
tutorialRemaining=AndroidTouchPolicy.TutorialRemaining(tutorialRemaining,false,180);
Check(Math.Abs(tutorialRemaining-4.9f)<1e-5f,"Brief World before a long intro preserves the remaining visible hint time");
tutorialRemaining=AndroidTouchPolicy.TutorialRemaining(tutorialRemaining,true,2);
tutorialRemaining=AndroidTouchPolicy.TutorialRemaining(tutorialRemaining,false,60);
Check(Math.Abs(tutorialRemaining-2.9f)<1e-5f,"Pause, inventory and dialogue do not consume the gameplay hint countdown");
Check(AndroidTouchPolicy.TutorialRemaining(tutorialRemaining,true,3)==0 && AndroidTouchPolicy.TutorialRemaining(0,true,1)==0,"Hints expire after five total controllable seconds and stay expired");
Check(AndroidTouchPolicy.TutorialAlpha(2)==1 && AndroidTouchPolicy.TutorialAlpha(1)==1 && AndroidTouchPolicy.TutorialAlpha(.5f)==.5f && AndroidTouchPolicy.TutorialAlpha(0)==0,"The last gameplay second smoothly fades the tutorial and edge regions to zero");
Check(!AndroidTouchPolicy.DocumentPage(true,0,0,true) && !AndroidTouchPolicy.DocumentPage(true,0,0,false),"A single page never flashes Previous/Next controls");
Check(!AndroidTouchPolicy.DocumentPage(true,0,2,true) && AndroidTouchPolicy.DocumentPage(true,0,2,false) && AndroidTouchPolicy.DocumentPage(true,1,2,true) && AndroidTouchPolicy.DocumentPage(true,1,2,false) && !AndroidTouchPolicy.DocumentPage(true,2,2,false),"Only authored available document pages expose navigation");
string pauseSettings="1:Root\u001fGAMEMANAGER\u001fCanvas (Options menu)\u001fUI\u001fPauseUI\u001fPausePanel\u001fBTN_Options";
Check(AndroidTouchPolicy.PauseSettings(pauseSettings) && !AndroidTouchPolicy.PauseSettings(pauseSettings.Replace("1:","0:")) && !AndroidTouchPolicy.PauseSettings(pauseSettings.Replace("BTN_Options","BTN_Resume")),"QA unlock accepts only Settings in the in-game pause menu");
var qaHold=new AndroidPauseSettingsHold();
qaHold.Begin(1,0,10,10,true);Check(!qaHold.Sample(1,.2f,10,10,true,true,20),"Short Settings tap remains an ordinary click");
qaHold.Begin(1,0,10,10,true);Check(!qaHold.Sample(1,4.99f,10,10,true,false,20) && qaHold.Sample(1,5,10,10,true,false,20) && !qaHold.Sample(1,6,10,10,true,false,20),"A stationary five second hold unlocks QA exactly once");
qaHold.Begin(1,0,10,10,false);Check(!qaHold.Sample(1,6,10,10,true,false,20),"Holding any other menu item cannot unlock QA");
qaHold.Begin(1,0,10,10,true);Check(!qaHold.Sample(1,.5f,40,10,true,false,20) && !qaHold.Sample(1,6,10,10,true,false,20),"Dragging off Settings cancels the hidden hold");
qaHold.Begin(1,0,10,10,true);qaHold.Cancel();Check(!qaHold.Sample(1,6,10,10,true,false,20),"Focus and context cancellation cannot leave an unlock pending");
qaHold.Begin(1,0,10,10,true);Check(!qaHold.Sample(1,6,10,10,false,false,20),"Leaving pause cancels a pending QA hold");
qaHold.Begin(-1,0,10,10,true);Check(qaHold.Sample(-1,5,10,10,true,false,20),"uGUI mouse-emulated touch identifiers also unlock QA");
var qaButton=new AndroidQaSettingsHold{Target=new UnityEngine.UI.Button()};
void QaTick(float now){Time.unscaledTime=now;typeof(AndroidQaSettingsHold).GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(qaButton,null);}
PointerEventData QaDown(int id=-1){Time.unscaledTime=0;var e=new PointerEventData(EventSystem.current){pointerId=id,position=new Vector2(10,10)};qaButton.OnPointerDown(e);return e;}
Input.Touches=Array.Empty<Touch>();var qaPress=QaDown();QaTick(4.99f);Check(!AndroidQaOrders.Enabled,"Pointer-based QA hold does not unlock before five unscaled seconds");
QaTick(5);QaTick(6);Check(AndroidQaOrders.Enabled&&AndroidTouchOverlay.QaNotices==1&&!qaPress.eligibleForClick,"The button owns a stationary hold without raw touches, unlocks once and consumes its click");
AndroidQaOrders.Enabled=false;qaPress=QaDown();QaTick(3);qaButton.OnPointerUp(qaPress);QaTick(6);Check(!AndroidQaOrders.Enabled&&qaPress.eligibleForClick,"Ordinary three second Settings press keeps the native settings click");
qaPress=QaDown(7);qaButton.OnPointerUp(new PointerEventData(EventSystem.current){pointerId=8});QaTick(5);Check(AndroidQaOrders.Enabled,"Another pointer release cannot cancel the captured Settings hold");
AndroidQaOrders.Enabled=false;qaPress=QaDown();qaButton.OnPointerExit(qaPress);QaTick(6);Check(!AndroidQaOrders.Enabled,"Leaving the Settings button cancels QA");
qaPress=QaDown();qaPress.position=new Vector2(100,10);QaTick(6);Check(!AndroidQaOrders.Enabled,"Dragging beyond the hold tolerance cancels QA");
qaPress=QaDown();AndroidContext.Game.Paused=false;QaTick(6);Check(!AndroidQaOrders.Enabled,"Leaving pause cancels the button-owned hold");AndroidContext.Game.Paused=true;
foreach(string callback in new[]{"OnDisable","OnApplicationFocus","OnApplicationPause"}){QaDown();object[] arguments=callback=="OnDisable"?null:new object[]{callback=="OnApplicationPause"};typeof(AndroidQaSettingsHold).GetMethod(callback,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(qaButton,arguments);QaTick(6);Check(!AndroidQaOrders.Enabled,"Disable, focus loss and Android pause cancel the Settings hold");}
qaButton.Target.interactable=false;QaDown();QaTick(6);Check(!AndroidQaOrders.Enabled,"Disabled Settings cannot unlock QA");qaButton.Target.interactable=true;
QaDown();qaButton.Target.interactable=false;QaTick(6);Check(!AndroidQaOrders.Enabled,"Disabling Settings during a hold cancels QA");qaButton.Target.interactable=true;
var photoClose=new AndroidReadingClose();
Check(!photoClose.Sample(false) && !photoClose.Sample(true) && !photoClose.Sample(true),"A pickup reader opening and staying open cannot consume its item early");
Check(photoClose.Sample(false) && !photoClose.Sample(false),"Only the actual first close consumes the photo once");
photoClose.Sample(true);photoClose.Reset();Check(!photoClose.Sample(false),"Scene replacement cannot consume a photo from another reader lifetime");
var unguardedStart=new DialogueOpeningFixture(false);unguardedStart.Update(true,true);
Check(!unguardedStart.Typing && unguardedStart.Text=="First sentence","Stock counterexample: one centre pulse in activationType.Start Update opens and immediately skips the first line");
var guardedStart=new DialogueOpeningFixture(true);guardedStart.Update(true,true);
Check(guardedStart.Typing && guardedStart.Text=="" && guardedStart.SkipCalls==0,"The opening press cannot skip the typewriter later in the same Start Update");
guardedStart.Frame++;guardedStart.Update(false,true);
Check(!guardedStart.Typing && guardedStart.Text=="First sentence" && guardedStart.SkipCalls==1,"A genuinely fresh next-frame tap retains authored skip behavior");
guardedStart.Update(false,true);guardedStart.Update(false,true);
Check(guardedStart.Typing && guardedStart.Text=="First sentence" && guardedStart.SkipCalls==1,"The press advancing to another sentence cannot skip it; authored prior text remains until the coroutine resumes");
var unguardedTrigger=new DialogueOpeningFixture(false);unguardedTrigger.TriggerDialogue();unguardedTrigger.Update(false,true);
Check(!unguardedTrigger.Typing && unguardedTrigger.Text=="First sentence","Stock counterexample: OnTriggerEnter followed by Update observes the same central input edge");
var guardedTrigger=new DialogueOpeningFixture(true);guardedTrigger.TriggerDialogue();guardedTrigger.Update(false,true);
Check(guardedTrigger.Typing && guardedTrigger.Text=="","A trigger or direct scene event followed by Update cannot reuse the opening press");
guardedTrigger.EndConvo();Check(guardedTrigger.Typing && !guardedTrigger.Closed,"Same-frame EndConvo preserves the running first sentence");
guardedTrigger.Frame++;guardedTrigger.EndConvo();Check(!guardedTrigger.Typing && !guardedTrigger.Closed && guardedTrigger.Text=="First sentence","The next-frame EndConvo first skips typing exactly as authored");
guardedTrigger.EndConvo();Check(guardedTrigger.Closed && guardedTrigger.Text=="","A subsequent authored EndConvo still closes and clears the dialogue");
var sentencePress=new AndroidDialoguePress();var firstManager=new object();var secondManager=new object();sentencePress.Begin(firstManager,70);
Check(!sentencePress.CanSkip(firstManager,70) && sentencePress.CanSkip(secondManager,70) && sentencePress.CanSkip(firstManager,71),"The guard belongs only to the marked manager and exact frame, with no time cooldown");
string openingDialogue="1:---------Sequences---------\u001f-----INTRO-----\u001fIntro dialogue";
Check(AndroidTouchPolicy.IsOpeningDialogue(openingDialogue) && !AndroidTouchPolicy.IsOpeningDialogue(openingDialogue.Replace("1:","2:")) && !AndroidTouchPolicy.IsOpeningDialogue("1:Apartment\u001fDialogue") && !AndroidTouchPolicy.IsOpeningDialogue(null),"Only the verified Scene1 opening trigger gates hints; absent and later dialogue owners do not");
tutorialRemaining=AndroidTouchPolicy.TutorialRemaining(5,!AndroidTouchPolicy.OpeningDialoguePending(true,false),12);
Check(tutorialRemaining==5,"Movement enabled before an unfinished intro cannot spend the five-second hint budget");
tutorialRemaining=AndroidTouchPolicy.TutorialRemaining(tutorialRemaining,!AndroidTouchPolicy.OpeningDialoguePending(true,true),2);
Check(tutorialRemaining==3 && AndroidTouchPolicy.TutorialRemaining(tutorialRemaining,false,180)==3,"Intro completion starts the countdown; later dialogue pauses it without resetting");
Check(!AndroidTouchPolicy.OpeningDialoguePending(false,false) && !AndroidTouchPolicy.OpeningDialoguePending(false,true),"Inactive saved-game intro or absent intro leaves hints ungated");
Check(Math.Abs(AndroidTouchPolicy.MenuFit(400,200,1200,600,1)-2.88f)<1e-5f,"Larger native settings fill 96 percent of safe screen height");
Check(Math.Abs(AndroidTouchPolicy.MenuFit(400,200,1200,600,.8f)-2.304f)<1e-5f && AndroidTouchPolicy.MenuFit(0,200,1200,600,1)==1,"Menu size preference and absent surface are bounded");
float menuMin=-325.5f,menuMax=267.5f;
float footerFit=AndroidTouchPolicy.MenuFit(910,menuMax-menuMin,1200,600,1);
float footerOffset=AndroidTouchPolicy.MenuOffset(menuMin*footerFit,menuMax*footerFit,0,600);
Check(Math.Abs(menuMin*footerFit+footerOffset-12)<1e-4f && Math.Abs(menuMax*footerFit+footerOffset-588)<1e-4f,"Asymmetric footer union fits fully with equal 2 percent margins");
Check(AndroidTouchPolicy.MenuOffset(100,500,70,570)==20,"Safe-area center offset respects asymmetric device insets");
float scaledHeight=(menuMax-menuMin)*2/3;
float resizedFit=AndroidTouchPolicy.MenuFit(910f*2/3,scaledHeight,800,360,.8f);
Check(Math.Abs(scaledHeight*resizedFit-360*.96f*.8f)<1e-4f,"Refitting after delayed Canvas rescale preserves chosen relative menu height");
Check(AndroidTouchPolicy.MoveRadius(1080,1)==135 && AndroidTouchPolicy.MoveRadius(720,1)==90,"Floating movement uses screen height and control size, independent of origin");
Check(AndroidTouchPolicy.DefaultFpsCap==60,"A fresh mobile profile defaults to60fps");
Check(AndroidTouchPolicy.FrameCap(60,120,true)==30&&AndroidTouchPolicy.FrameCap(0,120,true)==30&&AndroidTouchPolicy.FrameCap(30,60,false)==30&&AndroidTouchPolicy.FrameCap(60,120,false)==60&&AndroidTouchPolicy.FrameCap(0,90,false)==90,"Menus cap at30 to save CPU; gameplay retains explicit30/60/display choices");
Check(!AndroidTouchPolicy.ShouldRun(100,135) && AndroidTouchPolicy.ShouldRun(115,135) && !AndroidTouchPolicy.ShouldRun(0,0),"Outer stick travel runs; walking and absent radius do not");
Check(!AndroidTouchPolicy.HasInteraction(typeof(WallFixture)) && !AndroidTouchPolicy.HasInteraction(null),"A plain raycast wall has no interaction even when the game raycast returns it");
Check(AndroidTouchPolicy.HasInteraction(typeof(DoorFixture)) && AndroidTouchPolicy.HasInteraction(typeof(DerivedDoorFixture)),"Private authored UseObject and inherited receivers expose an action");
Check(AndroidTouchPolicy.HasInteraction(typeof(ItemFixture)) && AndroidTouchPolicy.HasInteraction(typeof(InteractiveItem)),"Item pickups handled by Update remain available without a UseObject method");
Check(AndroidTouchPolicy.HasInteraction(typeof(AdvancedDoorScript)) && AndroidTouchPolicy.HasInteraction(typeof(SlideShow_DialogueTrigger)),"Original doors and conversations poll the target in Update without a UseObject receiver");
Check(!AndroidTouchPolicy.HasInteraction(typeof(WrongReceiverFixture)),"Static and parameterized UseObject methods do not match the original message");
Check(AndroidTouchPolicy.InventoryGesture(-220,10,48500,2424,1080,.5f,.5f)==1 && AndroidTouchPolicy.InventoryGesture(220,10,48500,2424,1080,.5f,.5f)==-1,"Inventory horizontal swipes select in both directions");
Check(AndroidTouchPolicy.InventoryGesture(0,0,0,2424,1080,.2f,.1f)==-1 && AndroidTouchPolicy.InventoryGesture(0,0,0,2424,1080,.2f,.9f)==1 && AndroidTouchPolicy.InventoryGesture(0,0,0,2424,1080,.2f,.5f)==2,"Inventory side taps navigate; centre tap equips");
Check(AndroidTouchPolicy.InventoryGesture(5,250,62525,2424,1080,.3f,.5f)==0 && AndroidTouchPolicy.InventoryGesture(0,0,0,2424,1080,.8f,.5f)==0,"Vertical swipes and long holds never equip");
var triple=new AndroidThreeFingerTap();
bool Finger(int id,float at,int phase,float dx=0)=>triple.Sample(id,100+id*50+dx,200,at,phase,20);
Finger(1,0,0);Finger(2,.03f,0);Finger(3,.06f,0);
Check(triple.Captured&&!Finger(2,.1f,3)&&!Finger(3,.11f,3)&&Finger(1,.12f,3)&&!triple.Captured,"Simultaneous three-finger tap owns the gesture and crouches exactly once after all fingers release");
Check(!Finger(1,.13f,3),"Duplicate native release cannot repeat crouch");
for(int id=1;id<=3;++id){Finger(id,id,0);Check(!Finger(id,id+.05f,3),"Three sequential taps never crouch");}
Finger(1,0,0);Finger(2,1,0);Finger(3,1.02f,0);
Check(!triple.Captured&&!Finger(1,1.1f,3)&&!Finger(2,1.11f,3)&&!Finger(3,1.12f,3),"A third finger on held movement/look controls is not a simultaneous tap");
Finger(1,0,0);Finger(2,.02f,0);Finger(3,.04f,0);Finger(2,.1f,1,21);
Finger(1,.15f,3);Finger(2,.16f,3,21);Check(!Finger(3,.17f,3),"Three-finger swipe cannot crouch");
Finger(1,0,0);Finger(2,.02f,0);Finger(3,.04f,0);Finger(1,.5f,3);Finger(2,.51f,3);Check(!Finger(3,.52f,3),"Three-finger long hold cannot crouch");
Finger(1,0,0);Finger(2,.02f,0);Finger(3,.04f,0);Finger(4,.06f,0);Finger(4,.1f,3);Finger(1,.11f,3);Finger(2,.12f,3);Check(!Finger(3,.13f,3),"Four fingers never masquerade as a triple tap");
Finger(1,0,0);Finger(2,.02f,0);Finger(3,.04f,0);Finger(1,.1f,4);Finger(2,.11f,3);Check(!Finger(3,.12f,3),"OS cancellation cannot crouch");
Finger(1,0,0);Finger(2,.02f,0);Finger(3,.04f,0);triple.Cancel();Check(!triple.Captured&&!triple.HasTouches&&!Finger(1,.1f,3),"Context/focus reset cancels gesture ownership without an action");
var checkbox=new UnityEngine.UI.Toggle();var rowClick=new AndroidToggleRow{Target=checkbox};var click=new PointerEventData(EventSystem.current);
rowClick.OnPointerClick(click);Check(checkbox.isOn,"Whole row toggles checkbox");rowClick.OnPointerClick(click);Check(!checkbox.isOn,"Whole row toggles back once");
click.dragging=true;rowClick.OnPointerClick(click);Check(!checkbox.isOn,"Scrolling row does not toggle");click.dragging=false;
checkbox.interactable=false;rowClick.OnPointerClick(click);Check(!checkbox.isOn,"Unsupported effect remains disabled through row tap");checkbox.interactable=true;checkbox.active=false;rowClick.OnPointerClick(click);Check(!checkbox.isOn,"Inactive row cannot toggle");
checkbox.active=true;click.button=PointerEventData.InputButton.Right;rowClick.OnPointerClick(click);Check(!checkbox.isOn,"Row forwards only primary clicks");
float Filter(string name,float raw,bool android=true,bool mouse=false,int touches=1) => AndroidNativeMousePolicy.Filter(name,raw,android,mouse,touches);
Check(Filter("Mouse Y",.447f)==0,"No physical mouse: touchscreen delta must not rotate camera");
Check(Filter("Mouse X",12,false,false,1)==12,"Desktop mouse behavior preserved");
Check(Filter("Mouse X",12,true,true,0)==12,"Actual Android mouse with no active fingers preserved");
Check(Filter("Mouse Y",-12,true,true,0)==-12,"Actual Android mouse sign preserved");
Check(Filter("Mouse Y",12,true,true,1)==0,"Touch owns camera input while finger is down");
Check(Filter("Mouse Y",12,true,false,0)==0,"Mouse-absent post-touch native tail blocked");
Check(Filter("Horizontal",.7f)==.7f&&Filter("Vertical",-.2f)==-.2f,"Keyboard/non-mouse axes preserved");
Check(Filter("Custom Axis",3)==3,"Unknown authored axes preserved");
Check(Filter("Mouse ScrollWheel",1)==0&&Filter("Mouse ScrollWheel",1,true,true,0)==1,"Mouse wheel gate preserves real wheel");

var region=new AndroidTouchRegion();int begins=0,drags=0,ends=0,cancels=0;Vector2 observed=new(),delta=new();
object cameraModule=new();
region.begin=e=>++begins;
region.drag=e=>{++drags;observed=e.position;delta=e.delta;Check(ReferenceEquals(e.pointerPressRaycast.module,cameraModule),"Captured event camera/raycast preserved");};
region.end=e=>{++ends;Check(e!=null && e.position.Equals(new Vector2(11,75)),"Tap callback retains final finger position");};region.cancel=e=>++cancels;
var down=Event(3,10,20);down.pointerPressRaycast=new RaycastResult{module=cameraModule};
AndroidTouchOverlay.SuppressTouches=true;Begin(region,down);Check(begins==0,"Captured triple tap cannot activate inventory, pause or Use underneath");AndroidTouchOverlay.SuppressTouches=false;
Cursor.lockState=CursorLockMode.Locked;Time.frameCount=1;Begin(region,down);
Check(begins==1,"Touch begins through existing uGUI owner");
Input.Touches=new[]{Touch(3,TouchPhase.Moved,10,70,0,50)};Time.frameCount=2;
region.ProcessCapturedTouch();Check(drags==1&&observed.Equals(new Vector2(10,70))&&delta.Equals(new Vector2(0,50)),"Locked drag uses accumulated captured position displacement");
region.ProcessCapturedTouch();region.OnDrag(Event(3,1000,1000));Check(drags==1,"No duplicate locked fallback/uGUI dispatch in same frame");
Cursor.lockState=CursorLockMode.None;region.OnDrag(Event(3,1000,1000));Check(drags==1,"Unlock transition cannot double-dispatch same frame");
Time.frameCount=3;region.ProcessCapturedTouch();Check(drags==1,"Unlocked fallback does nothing");
var nativeDrag=Event(3,11,75);nativeDrag.pointerPressRaycast=down.pointerPressRaycast;region.OnDrag(nativeDrag);
Check(drags==2&&observed.Equals(new Vector2(11,75)),"Unlocked native uGUI drag preserved");
Cursor.lockState=CursorLockMode.Locked;Input.Touches=new[]{Touch(3,TouchPhase.Stationary,11,75,0,0)};Time.frameCount=4;
region.ProcessCapturedTouch();Check(drags==2&&ends==0&&cancels==0,"Stationary hold remains owned without fake drag");
Input.Touches=new[]{Touch(3,TouchPhase.Ended,11,75,0,0)};Time.frameCount=5;region.ProcessCapturedTouch();region.OnPointerUp(Event(3,11,75));
Check(ends==1&&cancels==0,"Ended releases once before duplicate native pointer-up");

Time.frameCount=6;Begin(region,down);Input.Touches=Array.Empty<Touch>();Time.frameCount=7;region.ProcessCapturedTouch();
Check(cancels==1&&ends==1,"Missing captured finger cancels and releases safely");
Time.frameCount=8;Begin(region,down);Input.Touches=new[]{Touch(3,TouchPhase.Canceled,10,20,0,0)};Time.frameCount=9;region.ProcessCapturedTouch();
Check(cancels==2,"Native canceled phase releases without waiting for uGUI");
Time.frameCount=10;Begin(region,down);region.Release(true);Input.Touches=new[]{Touch(3,TouchPhase.Moved,10,90,0,70)};Time.frameCount=11;region.ProcessCapturedTouch();
Check(cancels==3&&drags==2,"Context/focus cancellation prevents held-finger resurrection");

var move=new AndroidTouchRegion();var look=new AndroidTouchRegion();Vector2 movePos=new(),lookDelta=new();
move.drag=e=>movePos=e.position;look.drag=e=>lookDelta=e.delta;
Time.frameCount=12;Begin(move,Event(11,10,10));Begin(look,Event(12,90,10));
Input.Touches=new[]{Touch(12,TouchPhase.Moved,95,13,5,3),Touch(11,TouchPhase.Moved,10,80,0,70)};Time.frameCount=13;
move.ProcessCapturedTouch();look.ProcessCapturedTouch();
Check(movePos.Equals(new Vector2(10,80))&&lookDelta.Equals(new Vector2(5,3)),"Twin-stick fingers resolved by ID, independent of touch array order");
Begin(move,Event(12,99,99));Input.Touches=new[]{Touch(12,TouchPhase.Moved,99,99,4,86),Touch(11,TouchPhase.Moved,10,81,0,1)};Time.frameCount=14;move.ProcessCapturedTouch();
Check(movePos.Equals(new Vector2(10,81)),"Second finger cannot steal an existing owner");
var recovery=new AndroidTouchRegion();int recovered=0,recoveryEnds=0;Vector2 recoveredDelta=new();float totalY=0;
recovery.drag=e=>{++recovered;recoveredDelta=e.delta;totalY+=e.delta.y;};recovery.end=e=>++recoveryEnds;
Cursor.lockState=CursorLockMode.Locked;Time.frameCount=20;Begin(recovery,Event(25,5,10));
Input.Touches=new[]{Touch(25,TouchPhase.Stationary,5,30,0,2)};Time.frameCount=21;recovery.ProcessCapturedTouch();
Check(recovered==1&&recoveredDelta.Equals(new Vector2(0,20)),"Run36 counterexample: Stationary changed absolute position dispatches complete missing motion");
recovery.ProcessCapturedTouch();Check(recovered==1,"Recovered Stationary dispatch occurs once per frame");
Input.Touches=new[]{Touch(25,TouchPhase.Stationary,5,30,0,2)};Time.frameCount=22;recovery.ProcessCapturedTouch();
Check(recovered==1,"Stationary same position with stale nonzero native delta is not repeated");
Input.Touches=new[]{Touch(25,TouchPhase.Stationary,5,45,0,2)};Time.frameCount=23;recovery.ProcessCapturedTouch();
Check(recovered==2&&recoveredDelta.Equals(new Vector2(0,15)),"Consecutive Stationary positions accumulate their actual displacement");
Input.Touches=new[]{Touch(25,TouchPhase.Moved,5,60,0,3)};Time.frameCount=24;recovery.ProcessCapturedTouch();
Check(recovered==3&&recoveredDelta.Equals(new Vector2(0,15))&&totalY==50,"Stationary to Moved preserves sum of observed motion, not truncated native delta window");
Input.Touches=new[]{Touch(25,TouchPhase.Moved,5,60,0,3)};Time.frameCount=25;recovery.ProcessCapturedTouch();
Check(recovered==4&&recoveredDelta.Equals(Vector2.zero)&&totalY==50,"Moved at identical absolute position does not invent movement from stale native delta");
Input.Touches=new[]{Touch(25,TouchPhase.Ended,5,99,0,39)};Time.frameCount=26;recovery.ProcessCapturedTouch();recovery.OnPointerUp(Event(25,5,99));
Check(recovered==4&&recoveryEnds==1,"Ended position change releases once without an extra drag");

var gui=new AndroidTouchRegion();int guiDrags=0;PointerEventData guiObserved=null;Vector2 guiDelta=new();
gui.drag=e=>{++guiDrags;guiObserved=e;guiDelta=e.delta;};
Time.frameCount=30;Cursor.lockState=CursorLockMode.None;Begin(gui,Event(31,10,10));
var guiEvent=Event(31,10,40);guiEvent.delta=new Vector2(0,7);Time.frameCount=31;gui.OnDrag(guiEvent);
Check(guiDrags==1&&ReferenceEquals(guiObserved,guiEvent)&&guiDelta.Equals(new Vector2(0,7)),"Unlocked uGUI delivers the exact original event and authored delta");
Cursor.lockState=CursorLockMode.Locked;Input.Touches=new[]{Touch(31,TouchPhase.Stationary,10,40,0,7)};Time.frameCount=32;gui.ProcessCapturedTouch();
Check(guiDrags==1,"Unlocked to locked does not replay motion already delivered by uGUI");
Input.Touches=new[]{Touch(31,TouchPhase.Stationary,10,50,0,2)};Time.frameCount=33;gui.ProcessCapturedTouch();
Check(guiDrags==2&&guiDelta.Equals(new Vector2(0,10)),"Re-locked position recovery starts from most recent native uGUI event");
gui.Release(true);Input.Touches=new[]{Touch(31,TouchPhase.Stationary,10,80,0,30)};Time.frameCount=34;gui.ProcessCapturedTouch();
Check(guiDrags==2,"Canceled/released owner cannot revive from a changing Stationary finger");

var twinMove=new AndroidTouchRegion();var twinLook=new AndroidTouchRegion();Vector2 twinMovePos=new(),twinLookDelta=new();
twinMove.drag=e=>twinMovePos=e.position;twinLook.drag=e=>twinLookDelta=e.delta;
Time.frameCount=40;Begin(twinMove,Event(41,10,10));Begin(twinLook,Event(42,90,10));
Input.Touches=new[]{Touch(42,TouchPhase.Stationary,97,13,1,0),Touch(41,TouchPhase.Stationary,10,70,0,1)};Time.frameCount=41;
twinMove.ProcessCapturedTouch();twinLook.ProcessCapturedTouch();
Check(twinMovePos.Equals(new Vector2(10,70))&&twinLookDelta.Equals(new Vector2(7,3)),"Twin Stationary changed positions recover independently by captured finger ID");

var contextPress=new AndroidTouchRegion();int contextBegins=0;contextPress.begin=e=>++contextBegins;
Time.frameCount=50;Begin(contextPress,Event(50,10,10));contextPress.Release(true);Begin(contextPress,Event(50,10,10));
Check(contextBegins==1,"Context cancellation fences a duplicate Began delivered in the same frame");
Input.Touches=new[]{Touch(50,TouchPhase.Stationary,10,10,0,0)};Time.frameCount=51;contextPress.OnPointerDown(Event(50,10,10));
Input.Touches=new[]{Touch(50,TouchPhase.Moved,11,10,1,0)};Time.frameCount=52;contextPress.OnPointerDown(Event(50,11,10));
Check(contextBegins==1,"uGUI recreating the held finger after a context switch cannot exit TV, close a note or skip dialogue");
Input.Touches=Array.Empty<Touch>();Time.frameCount=53;contextPress.OnPointerDown(Event(50,11,10));Check(contextBegins==1,"An absent canceled finger cannot resurrect an overlay action");
Time.frameCount=54;Begin(contextPress,Event(50,11,10));Check(contextBegins==2,"A fresh real Began with the reused finger ID remains usable");contextPress.Release();
contextPress.OnPointerDown(Event(-1,11,10));Check(contextBegins==3,"Physical mouse down retains the native UI path");

RaycastHit Hit(MonoBehaviour component,float x,float z)
{component.gameObject.components.Add(component);return new RaycastHit{collider=new Collider{gameObject=component.gameObject},point=new Vector3(x,0,z)};}
var ray=new Ray(new Vector3(),new Vector3(0,0,1));
var exact=Hit(new RayItemFixture(),0,2);var broad=Hit(new RayItemFixture(),.1f,1);
Physics.Candidates=new[]{broad};Physics.SphereCalls=0;Physics.Cast=(r,d,l)=>exact;
Check(AndroidInteractionRaycast.Raycast(ray,out var picked,3,1) && picked.collider==exact.collider && Physics.SphereCalls==0,"An authored centre target always wins without a sphere query");
var door=Hit(new RayDoorFixture(),0,2);Physics.Cast=(r,d,l)=>door;
Check(AndroidInteractionRaycast.Raycast(ray,out picked,3,1) && picked.collider==door.collider && Physics.SphereCalls==0,"A centred door keeps its exact authored interaction");
Physics.Cast=(r,d,l)=>r.direction.x==0?null:broad;
Check(AndroidInteractionRaycast.Raycast(ray,out picked,3,1) && picked.collider==broad.collider,"A visible small item accepts the wider touch aim");
var wall=Hit(new RayWallFixture(),0,.8f);Physics.Cast=(r,d,l)=>wall;
Check(AndroidInteractionRaycast.Raycast(ray,out picked,3,1) && picked.collider==wall.collider,"A nearby wall prevents widened interaction through it");
var farther=Hit(new RayItemFixture(),.1f,2);Physics.Candidates=new[]{farther,broad};Physics.Cast=(r,d,l)=>r.direction.x==0?null:r.direction.x>.08f?broad:farther;
Check(AndroidInteractionRaycast.Raycast(ray,out picked,3,1) && picked.collider==broad.collider,"Unsorted sphere hits choose the nearest visible item");
Physics.Candidates=new[]{door};Physics.Cast=(r,d,l)=>null;
Check(!AndroidInteractionRaycast.Raycast(ray,out picked,3,1),"A door beside the crosshair is never widened");
var child=Hit(new RayItemFixture(),.1f,2);child.collider.gameObject.transform.parent=door.collider.gameObject.transform;Physics.Candidates=new[]{child};Physics.Cast=(r,d,l)=>r.direction.x==0?null:child;
Check(!AndroidInteractionRaycast.Raycast(ray,out picked,3,1),"A child collider of a door keeps the door exception");
var dynamicDoor=Hit(new DynamicObject{dynamicType=DynamicKind.Door},.1f,2);Physics.Candidates=new[]{dynamicDoor};Physics.Cast=(r,d,l)=>r.direction.x==0?null:dynamicDoor;
Check(!AndroidInteractionRaycast.Raycast(ray,out picked,3,1),"Generic DynamicObject doors also keep their original aim area");
var outside=Hit(new RayItemFixture(),.1f,3.1f);Physics.Candidates=new[]{outside};Physics.Cast=(r,d,l)=>r.direction.x==0?null:outside;
Check(!AndroidInteractionRaycast.Raycast(ray,out picked,3,1),"Widening never extends the authored reach distance");
Physics.Candidates=Enumerable.Repeat(broad,64).ToArray();Physics.Cast=(r,d,l)=>r.direction.x==0?null:broad;
Check(!AndroidInteractionRaycast.Raycast(ray,out picked,3,1),"A saturated nonalloc query preserves the exact ray rather than selecting a truncated arbitrary hit");
Physics.Candidates=new[]{broad};Physics.SphereCalls=0;Application.platform=RuntimePlatform.WindowsPlayer;Physics.Cast=(r,d,l)=>null;
Check(!AndroidInteractionRaycast.Raycast(ray,out picked,3,1) && Physics.SphereCalls==0,"Desktop interaction remains the original raycast");Application.platform=RuntimePlatform.Android;

Console.WriteLine($"TOUCH_FIX_FIXTURE_PASS assertions={assertions} lockedActualDrag=true twinFinger=true releaseMissingCancel=true noDuplicate=true mouseKeyboardDesktopPolicy=true");

static PointerEventData Event(int id,float x,float y)=>new PointerEventData(EventSystem.current){pointerId=id,position=new Vector2(x,y),button=PointerEventData.InputButton.Left};
static Touch Touch(int id,TouchPhase phase,float x,float y,float dx,float dy)=>new Touch{fingerId=id,phase=phase,position=new Vector2(x,y),deltaPosition=new Vector2(dx,dy)};
static void Begin(AndroidTouchRegion region,PointerEventData e){Input.Touches=new[]{Touch(e.pointerId,TouchPhase.Began,e.position.x,e.position.y,0,0)};region.OnPointerDown(e);}

class WallFixture { }
class DoorFixture { private void UseObject() { } }
class DerivedDoorFixture : DoorFixture { }
class AM_Item { }
class ItemFixture : AM_Item { }
class InteractiveItem { }
class AdvancedDoorScript { }
class SlideShow_DialogueTrigger { }
class WrongReceiverFixture { public static void UseObject() { } private void UseObject(int value) { } }
class RayItemFixture:MonoBehaviour {private void UseObject() { }}
class RayDoorFixture:MonoBehaviour {private void UseObject() { }}
class RayWallFixture:MonoBehaviour { }
enum DynamicKind {Door,Drawer}
class DynamicObject:MonoBehaviour {public DynamicKind dynamicType;private void UseObject() { }}

// These are the stock opening/typing/EndConvo branches, with Unity's synchronous first coroutine step.
class DialogueOpeningFixture
{
    readonly bool guarded;
    readonly AndroidDialoguePress press=new AndroidDialoguePress();
    readonly string[] sentences={"First sentence","Second sentence"};
    bool autoStarted,started;
    int line;
    public int Frame,SkipCalls;
    public bool Typing,Closed;
    public string Text="";
    public DialogueOpeningFixture(bool guarded){this.guarded=guarded;}
    void StartSentence()
    {
        if(guarded)press.Begin(this,Frame);
        var coroutine=TypeSentence(sentences[line]);coroutine.MoveNext();
    }
    System.Collections.IEnumerator TypeSentence(string sentence)
    {Typing=true;yield return null;Text=sentence;Typing=false;}
    public void TriggerDialogue()
    {
        if(!started){started=true;line=0;StartSentence();}
        else if(Typing)SkipToDisplayAll(sentences[line]);
        else if(line+1<sentences.Length){++line;StartSentence();}
        else EndConvo();
    }
    void SkipToDisplayAll(string sentence)
    {if(guarded && !press.CanSkip(this,Frame))return;Typing=false;Text=sentence;++SkipCalls;}
    public void EndConvo()
    {if(!started)return;if(Typing){SkipToDisplayAll(sentences[line]);return;}started=false;Text="";Closed=true;}
    public void Update(bool activationStart,bool down)
    {if(activationStart && !autoStarted){autoStarted=true;TriggerDialogue();}if(started && down)TriggerDialogue();}
}
