// FirstPersonLoD - stage 1 flat first-person camera mod for Legend of Dungeon (Unity 4.6, BepInEx 5)
//
// Design notes:
// - Zero Harmony patches. CamOTron (the game's camera controller) keeps running untouched,
//   so fades, floor teleports and menu camera moves all still work. We simply overwrite the
//   camera transform in OnPreCull, which fires after every Update/LateUpdate in the frame
//   (including CamOTron's pixel-snap in LateUpdate), immediately before culling. We always
//   win the frame without patching anything.
// - FP only engages while the game is actually being played (CamOTron.Playing and at least
//   one player present). In menus, or while Kami.Inst.Paused, the game camera behaves
//   normally and the cursor is released.
// - Sprites are quads that only face +Z/-Z (flipped by a 180 parent Y-rotation). There is no
//   billboard code in the game, so we yaw every Ani flipbook quad toward the camera here.
//
// Keys (defaults, change in BepInEx/config/FirstPersonLoD.cfg):
//   F6  toggle first person
//   F7  save current tuning back to the cfg file
//   F8  cycle billboard mode (off / free / quantized 45)
//   F9  toggle hiding your own sprite
//   F10 flip billboard facing 180 (use if all sprites render backwards)
//   F11 input diagnostics overlay (joysticks, cInput bindings, movement-lock flags)
//   F12 default branch: clear controller claims. Beta: toggle sprite thickness (PageUp/PageDown adjust)
//   -/= eye height down/up      ,/. FOV down/up
//
// Input guard (InputGuard class, config InputFix=true):
//   Root cause of "menus work, character won't move" (vanilla game, not BepInEx): Setup.ResetControl(n)
//   gives player n JOYSTICK-ONLY bindings pinned to Unity joystick n whenever
//   Input.GetJoystickNames().Length >= n, and gives P1 the keyboard only when zero joysticks exist.
//   Any phantom device (SteamVR, Oculus, ViGEm, Steam Input virtual pad, or an empty ghost entry that
//   Unity 4 keeps for unplugged devices) therefore strips P1's keyboard and pins P1 to a dead device,
//   while the real pad sits at a higher index that only P2+ read. cInput persists bindings to the
//   registry, so a bad mapping survives relaunches.
//   Guard policy: P1 always has a keyboard binding; a joystick is assigned to a player only after it
//   presses a button (phantoms never do), in press order: first pad joins P1 alongside the keyboard,
//   next pads become P2..P4. Joystick list change clears claims. Enforced by re-checking bindings
//   every second through cInput's public API; no Harmony.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

#if BETA
[BepInPlugin("com.sampatek.lod.firstperson", "LoD First Person", "0.10.4")]
#else
[BepInPlugin("com.sampatek.lod.firstperson", "LoD First Person", "0.2.1")]
#endif
public class FirstPersonPlugin : BaseUnityPlugin
{
    private bool overlayOn;
    private bool appFocused = true;
    private string overlayText = "";
    private float overlayRefreshAt;
    private float overlayLogAt;

#if BETA
    public const string BuildTag = "v0.10.4 (beta branch, Unity 5.6 + SteamVR)";
#else
    public const string BuildTag = "v0.2.1 (default branch, Unity 4.6)";
#endif

    private static FieldInfo fiMoveable;
    private static FieldInfo fiGrounded;
    private static FieldInfo fiCanjump;

    private void Awake()
    {
        FPConfig.Load();
        BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
#if BETA
        // Beta build is obfuscated: private members carry the names from LegendofDungeon.exe_ObfuscatorLog.txt
        fiMoveable = typeof(PlayerMovement2).GetField("AAAAAAAAAAAAAAAAAAib", any); // moveable
        fiGrounded = typeof(PlayerMovement2).GetField("grounded", any);
        fiCanjump  = typeof(PlayerMovement2).GetField("AAAAAAAAAAAAAAAAin", any);   // canjump
        VRBindings.Ensure();
        VRFP.Init();
        ForgivingInput.Install();
#else
        fiMoveable = typeof(PlayerMovement2).GetField("moveable", any);
        fiGrounded = typeof(PlayerMovement2).GetField("grounded", any);
        fiCanjump  = typeof(PlayerMovement2).GetField("canjump",  any);
#endif
        Debug.Log("[FirstPersonLoD] " + BuildTag + " loaded. Toggle: " + FPConfig.ToggleKey
            + ", diagnostics: F11, InputFix: " + FPConfig.InputFix);
    }

    private void OnApplicationFocus(bool focus) { appFocused = focus; }
#if BETA
#endif

#if BETA
    private void LateUpdate() { FramePerf.MarkLate(); VRFP.Tick(); }
    private void FixedUpdate() { FramePerf.MarkFixed(); }
#endif

    private void Update()
    {
#if BETA
        FramePerf.MarkUpdate();
        AudioGuard.Tick();
#endif
#if !BETA
        if (FPConfig.InputFix) InputGuard.Tick();
#endif

        if (Input.GetKeyDown(KeyCode.F11))
        {
            overlayOn = !overlayOn;
            Debug.Log("[FirstPersonLoD] diagnostics overlay " + (overlayOn ? "on" : "off"));
        }
        if (overlayOn && Time.realtimeSinceStartup > overlayRefreshAt)
        {
            overlayText = BuildOverlay();
            overlayRefreshAt = Time.realtimeSinceStartup + 0.25f;
            if (Time.realtimeSinceStartup > overlayLogAt)
            {
                Debug.Log("[FirstPersonLoD] DIAG\n" + overlayText);
                overlayLogAt = Time.realtimeSinceStartup + 3f;
            }
        }

        FPDriver d = FPDriver.Instance;
        if (d == null)
        {
            // Camera object gets rebuilt on scene loads; re-attach whenever it exists.
            CamOTron cot = (CamOTron)UnityEngine.Object.FindObjectOfType(typeof(CamOTron));
            if (cot != null && cot.cam != null)
            {
                FPDriver nd = cot.cam.gameObject.GetComponent<FPDriver>();
                if (nd == null) nd = cot.cam.gameObject.AddComponent<FPDriver>();
                nd.Init(cot);
            }
            return;
        }
        if (Input.GetKeyDown(FPConfig.ToggleKey)) d.Toggle();
    }

    // ---------------- input diagnostics ----------------

    private static string PausedStr()
    {
        try { return (Kami.Inst != null && Kami.Inst.Paused).ToString(); }
        catch (Exception) { return "?"; }
    }

    private static string TryRawAxis(string name)
    {
        try { return Input.GetAxisRaw(name).ToString("0.##"); }
        catch (Exception) { return "ERR"; }
    }

    private static string TrycAxis(string name)
    {
        try { return cInput.GetAxis(name).ToString("0.##"); }
        catch (Exception e) { return "ERR:" + Short(e); }
    }

    private static string TrycBtn(string name)
    {
        try { return cInput.GetButton(name) ? "DOWN" : "up"; }
        catch (Exception e) { return "ERR:" + Short(e); }
    }

    private static string TrycText(string action)
    {
        string a; string b;
        try { a = cInput.GetText(action, 1); } catch (Exception e) { a = "ERR:" + Short(e); }
        try { b = cInput.GetText(action, 2); } catch (Exception) { b = "?"; }
        return a + " / " + b;
    }

    private static string Short(Exception e)
    {
        string m = e.Message;
        if (m != null && m.Length > 40) m = m.Substring(0, 40);
        return m;
    }

    private static string RefBool(FieldInfo fi, object o)
    {
        if (fi == null) return "?";
        try { return fi.GetValue(o).ToString(); } catch (Exception) { return "?"; }
    }

    private string HeldKeys()
    {
        StringBuilder sb = new StringBuilder();
        KeyCode[] keys = new KeyCode[] {
            KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow,
            KeyCode.Z, KeyCode.X, KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.Space, KeyCode.Return
        };
        for (int i = 0; i < keys.Length; i++)
            if (Input.GetKey(keys[i])) { sb.Append(keys[i]); sb.Append(' '); }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    private string BuildOverlay()
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            sb.AppendLine("FirstPersonLoD diagnostics  (F11 closes, snapshot logged every 3s)");
            string playing = "?";
            string players = "?";
            try
            {
                CamOTron cot = (CamOTron)UnityEngine.Object.FindObjectOfType(typeof(CamOTron));
                if (cot != null)
                {
                    playing = cot.Playing.ToString();
                    players = (cot.Players == null) ? "null" : cot.Players.Length.ToString();
                }
            }
            catch (Exception) { }
            sb.AppendLine("focus:" + appFocused
                + "  paused:" + PausedStr()
                + "  Playing:" + playing + "  Players:" + players
                + "  cursorLock:" + Compat.CursorLocked);

            string[] joys;
            try { joys = Input.GetJoystickNames(); } catch (Exception) { joys = new string[0]; }
            sb.Append("joysticks(" + joys.Length + "):");
            for (int i = 0; i < joys.Length; i++) sb.Append("  [" + i + "] " + joys[i]);
            sb.AppendLine();

#if BETA
            sb.AppendLine(VRFP.Describe());
            sb.AppendLine("Forgiving P1 input: " + ForgivingInput.Note);
#else
            sb.AppendLine(InputGuard.Describe());
#endif
            sb.AppendLine("keys held: " + HeldKeys());
            sb.AppendLine("raw Unity axes  Horizontal:" + TryRawAxis("Horizontal") + "  Vertical:" + TryRawAxis("Vertical"));

            UnityEngine.Object[] pms = UnityEngine.Object.FindObjectsOfType(typeof(PlayerMovement2));
            sb.AppendLine("PlayerMovement2 instances: " + pms.Length);
            int shown = 0;
            for (int i = 0; i < pms.Length && shown < 4; i++)
            {
                PlayerMovement2 p = (PlayerMovement2)pms[i];
                if (p == null) continue;
                shown++;
                string num = p.num;
                sb.AppendLine("P[" + num + "]  speed:" + p.speed.ToString("0.##")
                    + "  moveable:" + RefBool(fiMoveable, p)
                    + "  freeze:" + p.freezesucka
                    + "  conf:" + p.Confusion
                    + "  asleep:" + p.asleep
                    + "  ghost:" + p.ghost
                    + "  grounded:" + RefBool(fiGrounded, p)
                    + "  canjump:" + RefBool(fiCanjump, p));
                sb.AppendLine("    cInput now  horz:" + TrycAxis("horz" + num) + "  vert:" + TrycAxis("vert" + num)
                    + "  Jump:" + TrycBtn("Jump" + num) + "  Use:" + TrycBtn("Use" + num));
                sb.AppendLine("    bound horz: " + TrycText("horz" + num));
                sb.AppendLine("    bound vert: " + TrycText("vert" + num));
                sb.AppendLine("    bound Jump: " + TrycText("Jump" + num) + "    Use: " + TrycText("Use" + num));
            }
            if (pms.Length == 0)
                sb.AppendLine("(no player spawned yet; join the game so P1 exists, then read this again)");
        }
        catch (Exception e)
        {
            sb.AppendLine("overlay error: " + e.Message);
        }
        return sb.ToString();
    }

    private void OnGUI()
    {
#if !BETA
        if (Time.realtimeSinceStartup < InputGuard.MsgUntil)
            GUI.Label(new Rect(10f, 26f, 900f, 30f), InputGuard.Msg);
#else
        if (Time.realtimeSinceStartup < VRFP.MsgUntil)
            GUI.Label(new Rect(10f, 26f, 900f, 30f), VRFP.Msg);
#endif
        if (!overlayOn) return;
        GUI.Box(new Rect(8f, 40f, 780f, 470f), "");
        GUI.Label(new Rect(16f, 46f, 764f, 458f), overlayText);
    }
}

#if !BETA
public static class InputGuard
{
    // Action suffixes the game defines per player (see Setup.ResetControl).
    private static readonly string[] Actions =
        { "Left", "Right", "Up", "Down", "Jump", "Use", "Next", "Previous", "Drop", "HotkeyA", "HotkeyB" };

    // Game's own P1 keyboard defaults (Setup.ResetControl, zero-joystick branch).
    private static readonly string[] P1Keys =
        { "LeftArrow", "RightArrow", "UpArrow", "DownArrow", "Z", "X", "A", "S", "D", "Q", "W" };

    // Game's own joystick templates. {0} = joystick number. Windows branch vs OSX branch.
    private static readonly string[] PadWin =
        { "Joy{0} Axis 1-", "Joy{0} Axis 1+", "Joy{0} Axis 2-", "Joy{0} Axis 2+",
          "Joystick{0}Button0", "Joystick{0}Button2", "Joystick{0}Button1", "Joystick{0}Button3",
          "Joystick{0}Button7", "Joystick{0}Button5", "Joystick{0}Button4" };
    private static readonly string[] PadMac =
        { "Joy{0} Axis 1-", "Joy{0} Axis 1+", "Joy{0} Axis 2-", "Joy{0} Axis 2+",
          "Joystick{0}Button16", "Joystick{0}Button18", "Joystick{0}Button17", "Joystick{0}Button19",
          "Joystick{0}Button9", "Joystick{0}Button14", "Joystick{0}Button13" };

    private const int MaxPads = 4;     // Unity 4 only has KeyCodes for joysticks 1..4
    private const int MaxPlayers = 4;

    // assigned[p] = joystick number (1..4) owned by player p (1..4), 0 = none
    private static int[] assigned = new int[MaxPlayers + 1];
    private static readonly List<int> fired = new List<int>(4);
    private static bool[] mirror = new bool[MaxPads + 1]; // fires in lockstep with another pad (Steam Input virtual copy)
    private static string signature;
    private static int churn;          // consecutive enforce passes that still had to change something
    private static bool halted;        // stop enforcing if bindings will not stick (protects the registry)
    private static bool ready;
    private static float armedAt = -1f;
    private static float nextCheck;
    private static int changeCount;
    private static int errCount;

    public static string Msg = "";
    public static float MsgUntil;

    private static void Say(string m)
    {
        Msg = "[FirstPersonLoD] " + m;
        MsgUntil = Time.realtimeSinceStartup + 4f;
        Debug.Log(Msg);
    }

    private static string JoySignature()
    {
        string[] n;
        try { n = Input.GetJoystickNames(); } catch (Exception) { return "?"; }
        return n.Length + ":" + string.Join("|", n);
    }

    private static bool CInputReady()
    {
        try
        {
            if (Setup.inst == null) return false;
            for (int p = 1; p <= MaxPlayers; p++)
                if (!cInput.IsKeyDefined("Left" + p)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool IsKeyboardBinding(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "None") return false;
        if (s.StartsWith("Joy") || s.StartsWith("Mouse")) return false;
        return true;
    }

    private static string Text(string action, int slot)
    {
        try { return cInput.GetText(action, slot); } catch (Exception) { return null; }
    }

    public static void ClearClaims(string why)
    {
        for (int p = 0; p <= MaxPlayers; p++) assigned[p] = 0;
        for (int j = 0; j <= MaxPads; j++) mirror[j] = false;
        churn = 0;
        halted = false;
        armedAt = Time.realtimeSinceStartup + 2f; // ignore stuck buttons reported at connect time
        nextCheck = 0f;
        Say("Controller claims cleared (" + why + "). Press any button on a controller to claim it.");
    }

    public static void Tick()
    {
        try { TickInner(); }
        catch (Exception e)
        {
            errCount++;
            if (errCount <= 5) Debug.LogError("[FirstPersonLoD] InputGuard: " + e);
        }
    }

    private static void TickInner()
    {
        if (!CInputReady()) { ready = false; return; }
        if (!ready)
        {
            ready = true;
            signature = JoySignature();
            ClearClaims("startup");
            Debug.Log("[FirstPersonLoD] InputGuard armed. Joysticks: " + signature);
        }

        if (Input.GetKeyDown(KeyCode.F12)) ClearClaims("F12");

        // Device list changed (plug/unplug, VR runtime started or stopped): indices may have shifted.
        if (Time.realtimeSinceStartup >= nextCheck)
        {
            string sig = JoySignature();
            if (sig != signature)
            {
                Debug.Log("[FirstPersonLoD] InputGuard: joystick list changed\n  was " + signature + "\n  now " + sig);
                signature = sig;
                ClearClaims("controller list changed");
            }
        }

        // Claim by button press. Phantom devices never press buttons.
        // Pads that fire on the same frame as another pad are one physical controller seen twice
        // (Steam Input exposes a virtual copy): claim only the first, mark the rest as mirrors.
        if (Time.realtimeSinceStartup >= armedAt)
        {
            bool ownedFired = false;
            int firstNew = 0;
            fired.Clear();
            for (int j = 1; j <= MaxPads; j++)
            {
                if (!AnyButtonDown(j)) continue;
                fired.Add(j);
                if (OwnerOf(j) != 0) ownedFired = true;
                else if (!mirror[j] && firstNew == 0) firstNew = j;
            }
            if (fired.Count > 0)
            {
                int claim = ownedFired ? 0 : firstNew;
                for (int i = 0; i < fired.Count; i++)
                {
                    int j = fired[i];
                    if (OwnerOf(j) == 0 && j != claim && !mirror[j] && fired.Count > 1)
                    {
                        mirror[j] = true;
                        Debug.Log("[FirstPersonLoD] InputGuard: joystick " + j + " (" + PadName(j)
                            + ") fired in lockstep with another pad; ignoring it as a duplicate.");
                    }
                }
                if (claim != 0)
                {
                    int p = FirstFreePlayer();
                    if (p != 0)
                    {
                        assigned[p] = claim;
                        nextCheck = 0f; // apply immediately
                        Say("Controller on joystick " + claim + " (" + PadName(claim) + ") is now Player " + p
                            + (p == 1 ? " (keyboard still works too)" : ". Press a button again to join."));
                    }
                }
            }
        }

        if (Time.realtimeSinceStartup >= nextCheck && !halted)
        {
            nextCheck = Time.realtimeSinceStartup + 1f;
            Enforce();
        }
    }

    private static int OwnerOf(int joy)
    {
        for (int p = 1; p <= MaxPlayers; p++) if (assigned[p] == joy) return p;
        return 0;
    }

    private static int FirstFreePlayer()
    {
        for (int p = 1; p <= MaxPlayers; p++) if (assigned[p] == 0) return p;
        return 0;
    }

    private static bool AnyButtonDown(int joy)
    {
        int b0 = 350 + (joy - 1) * 20; // KeyCode.Joystick1Button0 = 350, 20 buttons per joystick
        for (int b = 0; b < 20; b++)
            if (Input.GetKeyDown((KeyCode)(b0 + b))) return true;
        return false;
    }

    private static string PadName(int joy)
    {
        try
        {
            string[] n = Input.GetJoystickNames();
            if (joy - 1 < n.Length) return n[joy - 1].Length == 0 ? "(no name)" : n[joy - 1];
        }
        catch (Exception) { }
        return "?";
    }

    private static void Enforce()
    {
        bool mac = Application.platform == RuntimePlatform.OSXPlayer;
        string[] pad = mac ? PadMac : PadWin;
        int changed = 0;
        StringBuilder diffs = new StringBuilder();

        for (int p = 1; p <= MaxPlayers; p++)
        {
            int joy = assigned[p];
            for (int a = 0; a < Actions.Length; a++)
            {
                string action = Actions[a] + p;
                string cur1 = Text(action, 1);
                string cur2 = Text(action, 2);
                if (cur1 == null) continue;

                string padStr = joy > 0 ? string.Format(pad[a], joy) : "None";
                string want1, want2;
                if (p == 1)
                {
                    // Keep a custom keyboard key if the player set one; otherwise restore the default.
                    want1 = IsKeyboardBinding(cur1) ? cur1 : P1Keys[a];
                    want2 = padStr;
                }
                else
                {
                    want1 = padStr;
                    want2 = "None";
                }

                if (cur1 != want1 || cur2 != want2)
                {
                    if (diffs.Length < 600)
                        diffs.Append("\n  " + action + ": [" + cur1 + " / " + cur2 + "] -> [" + want1 + " / " + want2 + "]");
                    cInput.ChangeKey(action, want1, want2);
                    changed++;
                }
            }
        }
        if (changed > 0)
        {
            changeCount += changed;
            churn++;
            Debug.Log("[FirstPersonLoD] InputGuard enforced " + changed + " binding(s). " + Describe() + diffs);
            if (churn >= 5)
            {
                halted = true;
                Say("InputGuard stopped: bindings are not sticking. Send BepInEx\\LogOutput.log to Claude.");
                Debug.LogError("[FirstPersonLoD] InputGuard halted after 5 consecutive passes that did not converge.");
            }
        }
        else churn = 0;
    }

    public static string Describe()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("InputGuard: ");
        if (!FPConfig.InputFix) return sb.Append("OFF (InputFix=false)").ToString();
        if (!ready) return sb.Append("waiting for game input setup").ToString();
        if (halted) sb.Append("HALTED (bindings not sticking)  ");
        for (int p = 1; p <= MaxPlayers; p++)
        {
            sb.Append("P" + p + "=");
            if (p == 1) sb.Append("kbd");
            if (assigned[p] > 0) sb.Append((p == 1 ? "+" : "") + "joy" + assigned[p]);
            else if (p != 1) sb.Append("-");
            sb.Append("  ");
        }
        for (int j = 1; j <= MaxPads; j++) if (mirror[j]) sb.Append(" mirror:joy" + j);
        sb.Append(" enforced:" + changeCount);
        try
        {
            if (Input.GetJoystickNames().Length > MaxPads)
                sb.Append("\n  note: " + Input.GetJoystickNames().Length
                    + " joysticks listed; Unity 4 can only read joysticks 1-4. Disable phantom devices if your pad is 5+.");
        }
        catch (Exception) { }
        return sb.ToString();
    }
}

#endif

// ---------------------------------------------------------------------------------------------
// Sprite facing and thickness.
// Characters are quads animated by the game's Animate component (flipbook via material texture
// offset; Animate never touches rotation). Facing: each frame the quad is yawed toward the viewer.
// The game flips left/right by rotating a parent 180 on Y (double-sided shader), which KeepFlip
// preserves. Only moving entities (a Rigidbody or CharacterController above the quad) are turned,
// so animated wall torches and decals stay put (BillboardAll=true turns everything).
// Thickness: FPThick stacks copies of the quad along its facing axis. Copies share the original's
// material instance, so they animate in lockstep with no per-frame work; they only show while a
// first-person driver is active.
// ---------------------------------------------------------------------------------------------
public static class Sprites
{
    private static readonly List<Transform> list = new List<Transform>();
    private static float nextScan;
    private static Type[] itemTypes;
    private static Type[] spinTypes;
    private static int lastChars = -1;
    private static float nextCensus;

    // Loot and pickups are plain quads without the Animate flipbook (apples are PickupItem/Heal).
    private static readonly string[] ItemTypeNames = { "PickupItem", "Heal", "CoinGet", "XPGet", "Orb", "PotionItem",
        "Potion", "LanternGet", "shopitem", "Gotten" };
    // objects that spin on purpose keep their own rotation
    private static readonly string[] SpinTypeNames = { "SpinIt", "spinOnStart", "SpinPhysics" };

    private static Type[] Resolve(string[] names)
    {
        List<Type> r = new List<Type>();
        Assembly a = typeof(Animate).Assembly;
        for (int i = 0; i < names.Length; i++)
        {
            Type t = a.GetType(names[i], false);
            if (t != null && typeof(Component).IsAssignableFrom(t)) r.Add(t);
        }
        return r.ToArray();
    }

    private static bool Spins(Transform t)
    {
        for (Transform x = t; x != null; x = x.parent)
            for (int i = 0; i < spinTypes.Length; i++)
                if (x.GetComponent(spinTypes[i]) != null) return true;
        return false;
    }

    private const int MaxSprites = 1500;   // safety fuse: never manage more than this
    private static bool fuseBlown;

    private static readonly Dictionary<Transform, Quaternion> origRot = new Dictionary<Transform, Quaternion>();

    // put every sprite back the way the game had it (used when first person hands back to the tabletop)
    public static void RestoreFacing()
    {
        foreach (KeyValuePair<Transform, Quaternion> kv in origRot)
            if (kv.Key != null) kv.Key.localRotation = kv.Value;
    }

    private static void Add(Transform tr, Transform selfT)
    {
        if (!origRot.ContainsKey(tr)) origRot[tr] = tr.localRotation;
        if (origRot.Count > 4000)
        {
            List<Transform> dead = new List<Transform>();
            foreach (Transform k in origRot.Keys) if (k == null) dead.Add(k);
            for (int i = 0; i < dead.Count; i++) origRot.Remove(dead[i]);
        }
        if (tr.GetComponent<FPThickLayer>() != null) return;
        if (list.Count + building.Count >= MaxSprites)
        {
            if (!fuseBlown) { fuseBlown = true; Debug.LogError("[FirstPersonLoD] sprite cap " + MaxSprites + " reached; ignoring the rest (report this)"); }
            return;
        }
        if (selfT != null && tr.IsChildOf(selfT)) return;
        building.Add(tr);
        if (FPConfig.ThickOn && tr.GetComponent<FPThick>() == null && FPThick.Count < MaxSprites)
        {
            Renderer r = tr.GetComponent<Renderer>();
            MeshFilter mf = tr.GetComponent<MeshFilter>();
            if (r != null && mf != null && mf.sharedMesh != null)
            {
                if (!loggedShader)
                {
                    loggedShader = true;
                    Debug.Log("[FirstPersonLoD] sprite sample: " + tr.name + "  renderer " + r.GetType().Name
                        + "  shader " + (r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?")
                        + "  lossyScale " + tr.lossyScale.ToString("F3") + "  mesh " + mf.sharedMesh.bounds.size.ToString("F3"));
                }
                tr.gameObject.AddComponent<FPThick>();
            }
        }
    }
    private static bool loggedShader;
    public static int LastActiveFrame = -100;
    public static Vector3 ViewerPos;
    public static int CopiesDrawn;             // thickness copies enabled last frame (perf report)

    public static void Reset() { nextScan = 0f; scanStep = -1; list.Clear(); classified.Clear(); fullDone = false; }

    // The room you are in. With spawn hooks working, one whole-scene scan at the start is enough;
    // after that the backstop only looks through this room (a whole-scene FindObjectsOfType costs
    // tens of milliseconds, and the backstop used to run ~11 of them in a row every 10 s).
    private static Transform room;
    private static bool fullDone;
    private static double stepMsMax;
    private static readonly System.Diagnostics.Stopwatch stepWatch = new System.Diagnostics.Stopwatch();
    public static void SetRoom(GameObject r)
    {
        Transform t = r != null ? r.transform : null;
        if (t == room) return;
        room = t;
        if (scanStep < 0) nextScan = 0f;   // look through the new room right away
    }

    // F12 toggles thickness, PageUp/PageDown adjust it. Returns a status message or null.
    public static string HandleKeys()
    {
#if BETA
        KeyCode toggle = KeyCode.F12;
#else
        KeyCode toggle = KeyCode.Home;   // F12 is the controller-claim reset on the default branch
#endif
        if (Input.GetKeyDown(toggle)) { FPConfig.ThickOn = !FPConfig.ThickOn; Reset(); return "Sprite thickness " + (FPConfig.ThickOn ? "ON" : "OFF"); }
        if (Input.GetKeyDown(KeyCode.PageUp)) { FPConfig.ThickRatio = Mathf.Min(1f, FPConfig.ThickRatio + 0.04f); return "Sprite thickness " + FPConfig.ThickRatio.ToString("0.00"); }
        if (Input.GetKeyDown(KeyCode.PageDown)) { FPConfig.ThickRatio = Mathf.Max(0.02f, FPConfig.ThickRatio - 0.04f); return "Sprite thickness " + FPConfig.ThickRatio.ToString("0.00"); }
        return null;
    }

    private static bool IsEntity(Transform t)
    {
        for (Transform x = t; x != null; x = x.parent)
            if (x.GetComponent<Rigidbody>() != null || x.GetComponent<CharacterController>() != null) return true;
        return false;
    }

    // Sprites are registered when they spawn (Harmony postfixes on Animate.Start and each item
    // type's Start, beta) and by a slow backstop scan that is spread over frames, one
    // FindObjectsOfType per frame, every BackstopSeconds. FindObjectsOfType on script types walks
    // every object in the scene, so the old once-a-second scan cost several ms per frame.
    private static readonly List<Transform> building = new List<Transform>();
    private static readonly List<Component> pending = new List<Component>();
    private static readonly Dictionary<int, bool> classified = new Dictionary<int, bool>(); // instance id -> accepted
    private static int scanStep = -1;
    private static StringBuilder sample = new StringBuilder();
    private static Transform lastSelf;
    public static bool Hooked;
    private const float BackstopSeconds = 10f;

    public static void AnimateStarted(Animate __instance) { Queue(__instance); }
    public static void ItemStarted(Component __instance) { Queue(__instance); }
    private static void Queue(Component c)
    {
        if (c == null) return;
        if (pending.Count > 4000) { pending.Clear(); nextScan = 0f; } // not driving for a long time: rescan instead
        pending.Add(c);
    }

#if BETA
    // beta: register sprites as they spawn instead of polling
    public static string InstallHooks()
    {
        if (itemTypes == null) { itemTypes = Resolve(ItemTypeNames); spinTypes = Resolve(SpinTypeNames); }
        BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        BindingFlags ps = BindingFlags.Public | BindingFlags.Static;
        int ok = 0, bad = 0;
        string e = HarmonyShim.Postfix(typeof(Animate).GetMethod("Start", any), typeof(Sprites).GetMethod("AnimateStarted", ps));
        if (e == null) ok++; else bad++;
        for (int i = 0; i < itemTypes.Length; i++)
        {
            MethodInfo st = itemTypes[i].GetMethod("Start", any) ?? itemTypes[i].GetMethod("Awake", any);
            if (st == null) continue;
            if (HarmonyShim.Postfix(st, typeof(Sprites).GetMethod("ItemStarted", ps)) == null) ok++; else bad++;
        }
        Hooked = e == null;
        return ok + " spawn hooks" + (bad > 0 ? ", " + bad + " failed" : "") + (Hooked ? "" : " (Animate hook failed: " + e + ")");
    }
#endif

    private static bool Known(Transform t)
    {
        bool acc;
        return classified.TryGetValue(t.GetInstanceID(), out acc);
    }

    private static void Consider(Component c, bool isAnimate, Transform selfT)
    {
        if (c == null) return;
        if (isAnimate)
        {
            Transform t = c.transform;
            if (Known(t)) return;
            bool acc = FPConfig.BillboardAll || IsEntity(t);
            classified[t.GetInstanceID()] = acc;
            if (acc) Add(t, selfT);
            return;
        }
        MeshFilter[] mfs = c.GetComponentsInChildren<MeshFilter>();
        for (int j = 0; j < mfs.Length; j++)
        {
            MeshFilter mf = mfs[j];
            Transform t = mf.transform;
            if (Known(t)) continue;
            bool acc = mf.sharedMesh != null && mf.GetComponent<Renderer>() != null && mf.GetComponent<FPThickLayer>() == null
                && mf.sharedMesh.bounds.size.z <= 0.001f && Mathf.Abs(t.forward.y) <= 0.5f && !Spins(t);
            classified[t.GetInstanceID()] = acc;
            if (acc)
            {
                Add(t, selfT);
                if (sample.Length < 200) sample.Append(" " + c.name + "(" + c.GetType().Name + ")");
            }
        }
    }

    private static void Scan(GameObject self)
    {
        if (itemTypes == null) { itemTypes = Resolve(ItemTypeNames); spinTypes = Resolve(SpinTypeNames); }
        Transform selfT = self != null ? self.transform : null;
        if (selfT != lastSelf) { lastSelf = selfT; list.Clear(); classified.Clear(); nextScan = 0f; scanStep = -1; fullDone = false; }

        // spawn registrations (cheap, a few per frame)
        int budget = 32;
        while (pending.Count > 0 && budget-- > 0)
        {
            Component c = pending[pending.Count - 1];
            pending.RemoveAt(pending.Count - 1);
            if (c == null) continue;
            building.Clear();
            Consider(c, c is Animate, selfT);
            list.AddRange(building);
        }

        // backstop scan
        if (scanStep < 0)
        {
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + (Hooked ? BackstopSeconds : 1f);
            scanStep = 0;
            sample.Length = 0;
        }
        building.Clear();
        bool whole = !Hooked || !fullDone || room == null;
        stepWatch.Reset(); stepWatch.Start();
        if (scanStep == 0)
        {
            if (whole)
            {
                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(typeof(Animate));
                for (int i = 0; i < found.Length; i++) Consider((Animate)found[i], true, selfT);
            }
            else
            {
                Component[] found = room.GetComponentsInChildren(typeof(Animate));
                for (int i = 0; i < found.Length; i++) Consider(found[i], true, selfT);
            }
        }
        else
        {
            if (whole)
            {
                UnityEngine.Object[] items = UnityEngine.Object.FindObjectsOfType(itemTypes[scanStep - 1]);
                for (int i = 0; i < items.Length; i++) Consider(items[i] as Component, false, selfT);
            }
            else
            {
                Component[] items = room.GetComponentsInChildren(itemTypes[scanStep - 1]);
                for (int i = 0; i < items.Length; i++) Consider(items[i], false, selfT);
            }
        }
        stepWatch.Stop();
        if (stepWatch.Elapsed.TotalMilliseconds > stepMsMax) stepMsMax = stepWatch.Elapsed.TotalMilliseconds;
        list.AddRange(building);
        scanStep++;
        if (scanStep <= itemTypes.Length) return;
        scanStep = -1;
        string how = whole ? "whole scene" : "this room";
        if (whole && room != null) fullDone = true;

        // prune destroyed sprites
        list.RemoveAll(IsGone);
        if (list.Count != lastChars && Time.realtimeSinceStartup >= nextCensus)
        {
            nextCensus = Time.realtimeSinceStartup + 10f;
            lastChars = list.Count;
            Debug.Log("[FirstPersonLoD] sprites facing viewer: " + list.Count + " tracked (" + (Hooked ? "spawn hooks + " + BackstopSeconds + " s backstop" : "polling")
                + ", last scan: " + how + ", slowest step " + stepMsMax.ToString("0.0") + " ms)" + (sample.Length > 0 ? ", new items:" + sample : ""));
            stepMsMax = 0;
        }
    }

    private static bool IsGone(Transform t) { return t == null; }

    // called every frame by a first-person driver
    public static void Face(Vector3 camPos, GameObject self)
    {
        LastActiveFrame = Time.frameCount;
        ViewerPos = camPos;
        CopiesDrawn = 0;
        Scan(self);
        if (FPConfig.BillboardMode <= 0) return;
        float far = FPConfig.FarClip > 0f ? FPConfig.FarClip * FPConfig.FarClip : float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            Transform tr = list[i];
            if (tr == null || !tr.gameObject.activeInHierarchy) continue;
            Vector3 d = tr.position - camPos;
            d.y = 0f;
            float sq = d.sqrMagnitude;
            if (sq < 0.0001f || sq > far) continue;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            if (FPConfig.BillboardMode == 2) yaw = Mathf.Round(yaw / 45f) * 45f;
            bool flip = FPConfig.FlipBillboard;
            if (FPConfig.KeepFlip && tr.parent != null)
            {
                float py = tr.parent.eulerAngles.y;
                if (py > 90f && py < 270f) flip = !flip;
            }
            tr.rotation = Quaternion.Euler(0f, yaw + (flip ? 180f : 0f), 0f);
        }
    }
}

// marker on thickness copies so no scan ever treats a copy as a new sprite
public class FPThickLayer : MonoBehaviour { }

public class FPThick : MonoBehaviour
{
    public static int Count;
    private Renderer src;
    private MeshFilter srcMf;
    private Renderer[] copies = new Renderer[0];
    private Material lastMat;
    private float builtRatio = -1f;
    private int builtLayers = -1;
    private Vector3 builtScale;

    private void Awake()
    {
        src = GetComponent<Renderer>();
        srcMf = GetComponent<MeshFilter>();
        Count++;
    }

    private void OnDestroy()
    {
        Count--;
        for (int i = 0; i < copies.Length; i++)
            if (copies[i] != null) Destroy(copies[i].gameObject);
    }

    private void Build()
    {
        int n = Mathf.Clamp(FPConfig.ThickLayers, 0, 16);
        if (n != copies.Length)
        {
            for (int i = 0; i < copies.Length; i++) if (copies[i] != null) Destroy(copies[i].gameObject);
            copies = new Renderer[n];
            for (int i = 0; i < n; i++)
            {
                GameObject g = new GameObject("fpthick");
                g.AddComponent<FPThickLayer>();
                g.layer = gameObject.layer;
                g.transform.parent = transform;
                g.transform.localRotation = Quaternion.identity;
                g.transform.localScale = Vector3.one;
                MeshFilter mf = g.AddComponent<MeshFilter>();
                mf.sharedMesh = srcMf.sharedMesh;
                MeshRenderer mr = g.AddComponent<MeshRenderer>();
#if BETA
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
#else
                mr.castShadows = false;
#endif
                mr.receiveShadows = src.receiveShadows;
                mr.sharedMaterials = src.sharedMaterials;
                mr.enabled = false;
                copies[i] = mr;
            }
            lastMat = src.sharedMaterial;
        }
        // spread layers symmetrically through the thickness (the original quad sits at the middle)
        Vector3 ls = transform.lossyScale;
        float width = Mathf.Abs(ls.x) * srcMf.sharedMesh.bounds.size.x;
        float world = width * FPConfig.ThickRatio;
        float zs = Mathf.Abs(ls.z) > 0.0001f ? Mathf.Abs(ls.z) : 1f;
        for (int i = 0; i < n; i++)
        {
            float t = (i + 1f) / (n + 1f) - 0.5f;
            if (copies[i] != null) copies[i].transform.localPosition = new Vector3(0f, 0f, world * t / zs);
        }
        builtRatio = FPConfig.ThickRatio;
        builtLayers = n;
        builtScale = ls;
    }

    private void LateUpdate()
    {
        if (src == null || srcMf == null || srcMf.sharedMesh == null) return;
        bool on = FPConfig.ThickOn && Time.frameCount - Sprites.LastActiveFrame <= 2 && src.enabled
            && (transform.position - Sprites.ViewerPos).sqrMagnitude < FPConfig.ThickRange * FPConfig.ThickRange;
        if (on) Sprites.CopiesDrawn += copies.Length;
        if (on && (builtLayers != FPConfig.ThickLayers || builtRatio != FPConfig.ThickRatio
            || (transform.lossyScale - builtScale).sqrMagnitude > 0.000001f)) Build();
        if (on && src.sharedMaterial != lastMat)
        {
            Material[] ms = src.sharedMaterials;
            for (int i = 0; i < copies.Length; i++) if (copies[i] != null) copies[i].sharedMaterials = ms;
            lastMat = src.sharedMaterial;
        }
        for (int i = 0; i < copies.Length; i++)
            if (copies[i] != null && copies[i].enabled != on) copies[i].enabled = on;
    }
}

// Finds player 1. CamOTron.Players is not reliable: CamOTron.Follow calls the RefreshPlayers
// iterator without StartCoroutine (a no-op in the original game), so the array can stay empty
// while playing. Fall back to what RefreshPlayers would have found: objects tagged "Player",
// preferring PlayerMovement2.num == "1".
public static class PlayerFinder
{
    private static GameObject cached;
    private static float nextLook;
    public static string Note = "";

    private static bool IsP1(GameObject g)
    {
        if (g == null || !g.activeInHierarchy) return false;
        PlayerMovement2 pm = g.GetComponent<PlayerMovement2>();
        return pm != null && pm.num == "1";
    }

    // Player 1 whether alive or dead: the game re-tags a dead player "Ghost", and the tavern also
    // holds unjoined Player2..4 objects tagged "Player". Following any of those instead of player 1
    // is what left the view stuck on an idle character after dying.
    public static GameObject P1(CamOTron cot)
    {
        if (cached != null && IsP1(cached) && Time.realtimeSinceStartup < nextLook) return cached;
        nextLook = Time.realtimeSinceStartup + 0.5f;
        if (cot != null && cot.Players != null)
            for (int i = 0; i < cot.Players.Length; i++)
                if (IsP1(cot.Players[i])) { cached = cot.Players[i]; return cached; }
        GameObject first = null;
        string[] tags = { "Player", "Ghost" };
        for (int t = 0; t < tags.Length; t++)
        {
            GameObject[] tagged;
            try { tagged = GameObject.FindGameObjectsWithTag(tags[t]); } catch (Exception) { continue; }
            for (int i = 0; i < tagged.Length; i++)
            {
                GameObject g = tagged[i];
                if (g == null || !g.activeInHierarchy || g.GetComponent<PlayerMovement2>() == null) continue;
                if (IsP1(g))
                {
                    if (cached != g) Note = "player 1 found as '" + tags[t] + "'";
                    cached = g;
                    return g;
                }
                if (first == null) first = g;
            }
        }
        cached = first;
        return first;
    }
}

public class FPDriver : MonoBehaviour
{
    public static FPDriver Instance;
    public static bool WantFP; // survives scene reloads

    private CamOTron cot;
    private Camera cam;

    private bool fpOn;
    private bool driving;          // fpOn AND actually in control this frame
    private float yaw;
    private float pitch;

    private float origNear;
    private float origFov;
    private bool mouseAxesOk = true;
    private Vector3 lastMouse;

    private static readonly List<Renderer> hiddenBody = new List<Renderer>();
    private static readonly List<Renderer> disabledBody = new List<Renderer>();
    private static GameObject hiddenFor;
    private static float nextHideScan;

    private string toast = "";
    private float toastUntil;
    private int errCount;

    public void Init(CamOTron c)
    {
        cot = c;
        cam = c.cam;
        Instance = this;
        if (WantFP && !fpOn) TurnOn();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        RestoreBody();
    }

    public void Toggle()
    {
        if (fpOn) TurnOff(); else TurnOn();
        WantFP = fpOn;
    }

    private void TurnOn()
    {
        if (cot == null || cam == null) return;
        fpOn = true;
        origNear = cam.nearClipPlane;
        origFov = cam.fieldOfView;
        yaw = 0f;
        pitch = 0f;
        lastMouse = Input.mousePosition;
        Toast("First person ON  (eye " + FPConfig.EyeHeight.ToString("0.00") + ", fov " + FPConfig.Fov.ToString("0") + ")");
    }

    private void TurnOff()
    {
        fpOn = false;
        driving = false;
        if (cam != null)
        {
            cam.nearClipPlane = origNear;
            cam.fieldOfView = origFov;
        }
        RestoreBody();
        Compat.CursorLocked = false;
        Toast("First person OFF");
    }

    private GameObject ActivePlayer() { return PlayerFinder.P1(cot); }

    private bool GamePaused()
    {
        try { return Kami.Inst != null && Kami.Inst.Paused; }
        catch { return false; }
    }

    private void Update()
    {
        try { UpdateInner(); }
        catch (Exception e) { Fail(e); }
    }

    private void UpdateInner()
    {
        if (!fpOn) { driving = false; return; }

        HandleTuningKeys();

        GameObject p = ActivePlayer();
        bool paused = GamePaused();
        driving = (p != null) && cot.Playing && !paused;

        if (!driving)
        {
            if (Compat.CursorLocked) Compat.CursorLocked = false;
            return;
        }
        if (!Compat.CursorLocked) Compat.CursorLocked = true;

        // Mouse look. Primary: the standard "Mouse X/Y" axes (work while the cursor is
        // locked). Fallback if those axes were removed from the project: cursor stays
        // unlocked and we read position deltas.
        float mx = 0f, my = 0f;
        if (mouseAxesOk)
        {
            try
            {
                mx = Input.GetAxis("Mouse X");
                my = Input.GetAxis("Mouse Y");
            }
            catch
            {
                mouseAxesOk = false;
                Compat.CursorLocked = false;
                Toast("Mouse axes missing; using fallback look (cursor unlocked)");
            }
        }
        if (!mouseAxesOk)
        {
            Vector3 mp = Input.mousePosition;
            mx = (mp.x - lastMouse.x) * 0.05f;
            my = (mp.y - lastMouse.y) * 0.05f;
            lastMouse = mp;
        }

        yaw += mx * FPConfig.MouseSensitivity;
        pitch += (FPConfig.InvertY ? my : -my) * FPConfig.MouseSensitivity;
        if (pitch > 89f) pitch = 89f;
        if (pitch < -89f) pitch = -89f;
    }

    private void OnPreCull()
    {
        try { PreCullInner(); }
        catch (Exception e) { Fail(e); }
    }

    private void PreCullInner()
    {
        if (!driving || cam == null) return;
        GameObject p = ActivePlayer();
        if (p == null) return;

        if (FPConfig.HideOwnBody) HideBody(p); else RestoreBody();

        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 pos = p.transform.position
                    + Vector3.up * FPConfig.EyeHeight
                    + rot * new Vector3(0f, 0f, FPConfig.ForwardOffset);

        Transform t = cam.transform;
        t.position = pos;
        t.rotation = rot;
        cam.fieldOfView = FPConfig.Fov;
        cam.nearClipPlane = FPConfig.NearClip;
        if (FPConfig.FogStart > 0f) RenderSettings.fogStartDistance = FPConfig.FogStart;

        Billboards(pos, p);
    }

    public static void ResetSpriteCache() { Sprites.Reset(); }

    public static void Billboards(Vector3 camPos, GameObject self) { Sprites.Face(camPos, self); }

    public static void HideBody(GameObject p) { HideBody(p, null); }

    // Hides the player's own renderers except the held item subtree (keep). Rescans twice a second
    // so newly equipped hats etc. are hidden too.
    // Sprites are NOT disabled: the game's Animate only advances a flipbook while its renderer is
    // visible, and the last frame of an attack is what sends UnShot (ends the attack, re-arms the
    // weapon). A disabled renderer froze the first swing forever. Cutout sprites instead get an
    // alpha cutoff above 1 on their own material instance: every pixel is discarded but the
    // renderer still counts as visible, so animations and attack timing run exactly as before.
    // Anything without a cutoff property (particles, lights' flares) is disabled as before.
    private static readonly Dictionary<Material, float> clipped = new Dictionary<Material, float>();

    public static bool TryOriginalCutoff(Material m, out float c) { return clipped.TryGetValue(m, out c); }

    private static bool Clip(Renderer r)
    {
        Material[] ms = r.materials; // per-renderer instances (Animate already uses r.material)
        bool any = false;
        for (int i = 0; i < ms.Length; i++)
        {
            Material m = ms[i];
            if (m == null || !m.HasProperty("_Cutoff")) continue;
            if (!clipped.ContainsKey(m)) clipped[m] = m.GetFloat("_Cutoff");
            m.SetFloat("_Cutoff", 2f);
            any = true;
        }
        return any;
    }

    private static void Unclip(Renderer r)
    {
        Material[] ms = r.sharedMaterials;
        for (int i = 0; i < ms.Length; i++)
        {
            float c;
            if (ms[i] != null && clipped.TryGetValue(ms[i], out c)) { ms[i].SetFloat("_Cutoff", c); clipped.Remove(ms[i]); }
        }
    }

    private static bool IsClipped(Renderer r)
    {
        Material m = r.sharedMaterial;
        return m != null && clipped.ContainsKey(m) && m.GetFloat("_Cutoff") > 1f;
    }

    // Renderers without an alpha cutoff (the Knight's sword and body use Transparent/Bumped Diffuse)
    // used to be disabled, which stopped their Animate: the held item's Animate is what advances the
    // attack and sends UnShot, so the first swing never ended and every later one was refused. They
    // now get an invisible material instead: still rendered (counts as visible), draws nothing.
    private static readonly Dictionary<Renderer, Material[]> swapped = new Dictionary<Renderer, Material[]>();
    private static Material invisMat;
    private static bool invisTried;
    private static string lastBodyNote = "";

    private static Material Invisible()
    {
        if (invisMat != null || invisTried) return invisMat;
        invisTried = true;
        Shader sh = Shader.Find("Legacy Shaders/Transparent/Diffuse") ?? Shader.Find("Transparent/Diffuse")
            ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Legacy Shaders/Transparent/Bumped Diffuse");
        if (sh == null) { Debug.Log("[FirstPersonLoD] VR body: no transparent shader for hiding; falls back to disabling"); return null; }
        Texture2D clear = new Texture2D(1, 1, TextureFormat.ARGB32, false);
        clear.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
        clear.Apply();
        invisMat = new Material(sh);
        invisMat.name = "FPInvisible";
        invisMat.mainTexture = clear;
        if (invisMat.HasProperty("_Color")) invisMat.color = new Color(0f, 0f, 0f, 0f);
        Debug.Log("[FirstPersonLoD] VR body: invisible material uses " + sh.name);
        return invisMat;
    }

    private static bool Swap(Renderer r)
    {
        Material inv = Invisible();
        if (inv == null) return false;
        Material[] orig = r.sharedMaterials;
        swapped[r] = orig;
        Material[] n = new Material[Mathf.Max(orig.Length, 1)];
        for (int i = 0; i < n.Length; i++) n[i] = inv;
        r.sharedMaterials = n;
        return true;
    }

    public static Material OriginalMaterial(Renderer r)
    {
        Material[] orig;
        if (r != null && swapped.TryGetValue(r, out orig) && orig.Length > 0) return orig[0];
        return r != null ? r.sharedMaterial : null;
    }

    private static void Unswap(Renderer r)
    {
        Material[] orig;
        if (r != null && swapped.TryGetValue(r, out orig)) { r.sharedMaterials = orig; swapped.Remove(r); }
    }

    private static bool IsSwapped(Renderer r)
    {
        // Animate touches renderer.material every frame, which turns ours into a per-renderer
        // "FPInvisible (Instance)"; that copy is still invisible (colour alpha 0)
        Material m = r.sharedMaterial;
        return swapped.ContainsKey(r) && m != null && m.name.StartsWith("FPInvisible")
            && (!m.HasProperty("_Color") || m.color.a < 0.01f);
    }

    private static string ShaderOf(Renderer r)
    {
        Material m = r.sharedMaterial;
        return m != null && m.shader != null ? m.shader.name : "no material";
    }

    public static void HideBody(GameObject p, GameObject keep)
    {
        if (hiddenFor != p) { RestoreBody(); hiddenFor = p; nextHideScan = 0f; }
        if (Time.realtimeSinceStartup < nextHideScan) return;
        nextHideScan = Time.realtimeSinceStartup + 0.5f;
        Renderer[] rs = p.GetComponentsInChildren<Renderer>();
        Transform k = keep != null ? keep.transform : null;
        StringBuilder note = null;
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null) continue;
            bool known = hiddenBody.Contains(r);
            if (k != null && r.transform.IsChildOf(k))
            {
                if (known) { Unclip(r); Unswap(r); if (disabledBody.Contains(r)) { r.enabled = true; disabledBody.Remove(r); } hiddenBody.Remove(r); }
                continue;
            }
            if (known)
            {
                // the game may swap materials (ghost, hats): re-apply
                if (swapped.ContainsKey(r))
                {
                    if (IsSwapped(r)) { }
                    else if (r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("FPInvisible"))
                        { Material[] ms = r.sharedMaterials; for (int j = 0; j < ms.Length; j++) if (ms[j] != null && ms[j].HasProperty("_Color")) ms[j].color = new Color(0f, 0f, 0f, 0f); }
                    else { swapped.Remove(r); Swap(r); } // the game gave it a new material of its own
                }
                else if (!disabledBody.Contains(r) && !IsClipped(r)) Clip(r);
                continue;
            }
            if (!r.enabled) continue;
            hiddenBody.Add(r);
            string shader = ShaderOf(r);
#if BETA
            SpriteDump.Save(r, p.name);
#endif
            string how;
            if (Clip(r)) how = "clipped";
            else if (Swap(r)) how = "invisible material";
            else { r.enabled = false; disabledBody.Add(r); how = "DISABLED"; }
            if (note == null) note = new StringBuilder();
            note.Append("\n  " + r.name + " (" + r.GetType().Name + ", " + shader + ", " + (r.GetComponent<Animate>() != null ? "animated" : "static")
                + ") -> " + how);
        }
        if (note != null)
        {
            string n = p.name + ":" + note;
            if (n != lastBodyNote) { lastBodyNote = n; Debug.Log("[FirstPersonLoD] VR body hidden from your eyes, " + n); }
        }
    }

    public static void RestoreBody()
    {
        for (int i = 0; i < hiddenBody.Count; i++)
        {
            Renderer r = hiddenBody[i];
            if (r == null) continue;
            Unclip(r);
            Unswap(r);
            if (disabledBody.Contains(r)) r.enabled = true;
        }
        foreach (KeyValuePair<Renderer, Material[]> kv in swapped) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
        swapped.Clear();
        // materials whose renderer was destroyed or swapped
        foreach (KeyValuePair<Material, float> kv in clipped) if (kv.Key != null) kv.Key.SetFloat("_Cutoff", kv.Value);
        clipped.Clear();
        hiddenBody.Clear();
        disabledBody.Clear();
        hiddenFor = null;
        lastBodyNote = "";
    }

    private void HandleTuningKeys()
    {
        if (Input.GetKeyDown(KeyCode.Minus))
        { FPConfig.EyeHeight -= 0.05f; Toast("Eye height " + FPConfig.EyeHeight.ToString("0.00")); }
        if (Input.GetKeyDown(KeyCode.Equals))
        { FPConfig.EyeHeight += 0.05f; Toast("Eye height " + FPConfig.EyeHeight.ToString("0.00")); }
        if (Input.GetKeyDown(KeyCode.Comma))
        { FPConfig.Fov = Mathf.Clamp(FPConfig.Fov - 5f, 20f, 120f); Toast("FOV " + FPConfig.Fov.ToString("0")); }
        if (Input.GetKeyDown(KeyCode.Period))
        { FPConfig.Fov = Mathf.Clamp(FPConfig.Fov + 5f, 20f, 120f); Toast("FOV " + FPConfig.Fov.ToString("0")); }
        if (Input.GetKeyDown(KeyCode.F8))
        {
            FPConfig.BillboardMode = (FPConfig.BillboardMode + 1) % 3;
            FPDriver.ResetSpriteCache();
            Toast("Billboard mode " + FPConfig.BillboardMode + " (0 off, 1 free, 2 quantized)");
        }
        if (Input.GetKeyDown(KeyCode.F9))
        {
            FPConfig.HideOwnBody = !FPConfig.HideOwnBody;
            Toast("Hide own sprite: " + FPConfig.HideOwnBody);
        }
        if (Input.GetKeyDown(KeyCode.F10))
        {
            FPConfig.FlipBillboard = !FPConfig.FlipBillboard;
            Toast("Billboard flip 180: " + FPConfig.FlipBillboard);
        }
        if (Input.GetKeyDown(KeyCode.F7))
        {
            FPConfig.Save();
            Toast("Saved " + FPConfig.CfgPath());
        }
        string tk = Sprites.HandleKeys();
        if (tk != null) Toast(tk);
    }

    private void Toast(string msg)
    {
        toast = "[FirstPersonLoD] " + msg;
        toastUntil = Time.realtimeSinceStartup + 2.5f;
        Debug.Log(toast);
    }

    private void Fail(Exception e)
    {
        errCount++;
        if (errCount <= 5) Debug.LogError("[FirstPersonLoD] " + e);
        if (errCount == 5) Debug.LogError("[FirstPersonLoD] further errors suppressed");
    }

    private void OnGUI()
    {
        if (Time.realtimeSinceStartup < toastUntil)
            GUI.Label(new Rect(10f, 10f, 800f, 30f), toast);
    }
}

public static class Compat
{
#if BETA
    public static bool CursorLocked
    {
        get { return Cursor.lockState == CursorLockMode.Locked; }
        set { Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None; Cursor.visible = !value; }
    }
#else
    public static bool CursorLocked
    {
        get { return Screen.lockCursor; }
        set { Screen.lockCursor = value; }
    }
#endif
}

#if BETA
// ---------------------------------------------------------------------------------------------
// VR first person (beta branch only: Unity 5.6.7f1, SteamVR Unity Plugin 2.x, launched -vrmode openvr)
//
// How the game does VR (from the de-obfuscated Assembly-CSharp):
//   CamOTron.Start with VR=true disables the flat camera, activates VRRig (SteamVR rig), parents the
//   P1 HUD to FollowVRHead. CamOTron.Update then parks its own transform at the current room each
//   frame (+VRoffset; x follows players when VRmovewith) and LateUpdate pixel-snaps it (Kami.RoundVector).
//   World scale is VRScaleObj.localScale = VRScale/10. VRInputs.Update copies SteamVR actions into
//   public fields; PlayerMovement2.Update (P1, VR) adds VRInputs.Move.x to horz and -Move.y to vert.
//
// What this driver does while first person is active:
//   - detaches the rig root (topmost of VRScaleObj / VRRig above the headset camera) from CamOTron so
//     the room parking and pixel snapping no longer move it; restores it exactly on release
//   - scales the world so the player's eye height matches your measured eye height (auto) and places
//     the tracking space so your head sits at the character's eyes (recenter), then carries that
//     offset with the player every frame. Head tracking (lean, crouch, look) is never cancelled.
//   - rotates the Move stick by head yaw (Harmony postfix on VRInputs.Update) so forward is where you look
//   - billboards sprites toward the headset and hides your own sprite
//   Released (rig handed back to the game) in menus, while paused, or when no player exists.
//
// Keys: F6 toggle VR first person, F5 recenter, [ / ] snap turn, -/= eye height (rescales),
//       ,/. world scale multiplier, Home face into the room, F4 aim with head on/off,
//       Insert mirror the weapon view, F3 render path forward/game, ; and ' sword angle,
//       / sword grip end, F7 save. Controller: hold Hotkey1 + Hotkey2 for 1.5 s to recenter
//       (Steam Frame: hold the left View button 1.5 s).
// v0.5.0: sword in hand (6DOF, motion attacks, attack box follows the blade), forward rendering
//       with a light budget (the game is deferred), carried light and ambient lift, GPU timing
//       from the compositor, lunge follows the blade, stairs hook fixed (obfuscated name).
// v0.4.5: eye from the collision capsule, near clip in meters, head leash, face into the room on
//       start and after doors/stairs, compositor blink across teleports, per-pixel light and
//       shadow budget, weapon view proxy, player (and attack box) turned to head yaw.
// ---------------------------------------------------------------------------------------------
public static class VRFP
{
    public static bool Driving;          // read by the VRInputs postfix
    public static string Msg = "";
    public static float MsgUntil;

    private static bool enabledFP;
    private static bool inited;
    private static bool harmonyOk;
    private static string harmonyNote = "not attempted";

    private static CamOTron cot;
    private static Transform head;       // the tracked HMD camera
    private static Camera headCam;
    private static float origFar;
    private static Transform root;       // rig root we move while driving
    private static Transform origParent;
    private static Vector3 origLP, origLS;
    private static Quaternion origLR;
    private static bool detached;

    private static GameObject player;
    private static Vector3 rootOffset;   // root.position - player.position
    private static bool needRecenter;
    private static float origNear;
    private static float eyeOffset;      // eye height above player.transform.position, game units
    private static float bodyRadius;     // collision capsule radius, game units (head leash)
    private static string bodyNote = "";
    private static bool needFace;        // turn the view toward the room on the next tick
    private static string faceWhy = "";
    private static Vector3 lastPlayerPos;
    private static bool lastPosValid;
    private static GameObject lastRoom;
    // the game's own transition darkness (CamOTron.BlackDrop), shown through the headset compositor
    private static float mirrorAlpha;      // what the compositor shows now (0 = clear)
    private static bool compositorTouched; // a fade was sent since the last hand-back
    private static string fadeKind = "";   // current game fade: door / stairs / other
    private static float fadeStart, fadePeak;
    private static bool fadeShown, fadeCut;
    private static float lastDoorAt = -10f, lastStairsAt = -10f;
    private static GameObject hiddenDrop;  // BlackDrop object hidden from the world while first person drives
    private static GameObject examinedDrop; // BlackDrop object already looked at since engage
    private static bool dropWanted;        // the game has it active (so the tabletop would show it)
    private static string dropNote = "";
    private static bool aimApplied;
    private static float savedYaw;
    private static bool hasSavedYaw;
    private static GameObject savedRoom;
    private static float physEye;        // measured head height above tracking floor, meters
    private static float worldScale;     // game units per meter
    public static float WorldScale { get { return worldScale; } }
    public static GameObject Player { get { return player; } }
    public static Vector3 HeadForward { get { return head != null ? head.forward : Vector3.back; } }
    private static float comboHeld;
    private static int errCount;
    private static float nextInputDiag;
    private static ulong hTurnL, hTurnR;
    private static int turnState;          // 0 unresolved, 1 ok, -1 actions missing from manifest
    private static bool turnLeftWas, turnRightWas;
    private static bool leftFlickArmed = true;

    private static ulong hNextH, hPrevH, hJumpH, hRecenterH;
    private static float recenterHeld;
    private static float toggleHeld;
    private static float giveHeld;
    private static GameObject givenTo;

    // held state of a boolean action straight from OpenVR (VRInputs only keeps press events for these)
    private static bool ReadHeld(string action, ref ulong h, out bool held)
    {
        held = false;
        Valve.VR.CVRInput input = Valve.VR.OpenVR.Input;
        if (input == null) return false;
        if (h == 0) input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/" + action, ref h);
        if (h == 0) return false;
        Valve.VR.InputDigitalActionData_t d = new Valve.VR.InputDigitalActionData_t();
        input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ h, ref d,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputDigitalActionData_t)), 0);
        held = d.bActive && d.bState;
        return true;
    }

    private static bool ReadTurn(out bool left, out bool right)
    {
        left = right = false;
        if (turnState < 0) return false;
        Valve.VR.CVRInput input = Valve.VR.OpenVR.Input;
        if (input == null) return false;
        if (turnState == 0)
        {
            Valve.VR.EVRInputError e1 = input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/TurnLeft", ref hTurnL);
            Valve.VR.EVRInputError e2 = input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/TurnRight", ref hTurnR);
            bool ok = e1 == Valve.VR.EVRInputError.None && e2 == Valve.VR.EVRInputError.None && hTurnL != 0 && hTurnR != 0;
            turnState = ok ? 1 : -1;
            Debug.Log("[FirstPersonLoD] VR snap turn: action handles " + (ok ? "ok" : "failed (" + e1 + "/" + e2 + ")")
                + "; binding state is in the VR input report (TurnLeft/TurnRight BOUND = working)");
            if (!ok) return false;
        }
        uint dsize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputDigitalActionData_t));
        Valve.VR.InputDigitalActionData_t d = new Valve.VR.InputDigitalActionData_t();
        input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ hTurnL, ref d, dsize, 0);
        left = d.bActive && d.bState;
        d = new Valve.VR.InputDigitalActionData_t();
        input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ hTurnR, ref d, dsize, 0);
        right = d.bActive && d.bState;
        return true;
    }
    private static string lastIdleReason = "";
    private static string pendingMenuRecenter = "";
    private static bool probedOnce, warmedUp;
    private static string lastPlacedView = "";
    private static float pendingSince, trackedSince = -1f;
    private static string lastInputDiag = "";

    // ---- live input tracing: OpenVR raw -> SteamVR plugin -> game VRInputs ----
    private static bool handlesOk;
    private static ulong hMove, hJump, hUse;
    private static float nextActivityLog, lastHeartbeat;
    private static float rawMoveMax, plugMoveMax, vriMoveMax;
    private static int rawJump, rawUse, plugJump, plugUse, vriJump, vriUse, vriNull, frames;
    private static readonly Dictionary<uint, string> originSeen = new Dictionary<uint, string>();

    // Which physical button produced each game action, in SteamVR's own words ("Left Hand ...
    // X Button"): the first few presses of every action are logged, so a run where you press each
    // button once shows exactly what reaches the game (and what does not reach it at all).
    private static ulong[] pressHandles;
    private static bool[] pressWas;
    private static int[] pressLogged;
    private static readonly StringBuilder originName = new StringBuilder(256);
    private static readonly int[] heldFrames = new int[32], activeFrames = new int[32];
    private static bool warnedUnbound;

    // per report: how many frames each action was held, and a plain warning when every action is
    // unbound although the controllers still track (SteamVR has taken the input away from the game)
    private static string ButtonActivity(int framesInReport)
    {
        if (pressHandles == null) return "";
        StringBuilder sb = new StringBuilder();
        int active = 0;
        for (int i = 0; i < pressHandles.Length; i++)
        {
            if (heldFrames[i] > 0) sb.Append(" " + DigitalActions[i] + " " + heldFrames[i] + "f");
            if (activeFrames[i] > 0) active++;
            heldFrames[i] = 0; activeFrames[i] = 0;
        }
        string r = "\n  buttons held:" + (sb.Length > 0 ? sb.ToString() : " none");
        if (active == 0 && framesInReport > 30)
        {
            r += "\n  NO game action is bound right now: SteamVR is not giving this game any controller input"
                + " (its binding for the game was changed or removed, or the SteamVR dashboard has the controllers)";
            if (!warnedUnbound) { warnedUnbound = true; Debug.LogWarning("[FirstPersonLoD] VR: all game actions became unbound while the controllers are tracked"); }
        }
        else warnedUnbound = false;
        return r;
    }

    private static void LogPresses(Valve.VR.CVRInput input, uint dsize)
    {
        if (!FPConfig.LogButtons) return;
        if (pressHandles == null)
        {
            pressHandles = new ulong[DigitalActions.Length];
            pressWas = new bool[DigitalActions.Length];
            pressLogged = new int[DigitalActions.Length];
            for (int i = 0; i < DigitalActions.Length; i++)
                input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/" + DigitalActions[i], ref pressHandles[i]);
        }
        for (int i = 0; i < pressHandles.Length; i++)
        {
            if (pressHandles[i] == 0) continue;
            Valve.VR.InputDigitalActionData_t d = new Valve.VR.InputDigitalActionData_t();
            input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ pressHandles[i], ref d, dsize, 0);
            bool down = d.bActive && d.bState;
            if (down) heldFrames[i]++;
            if (d.bActive) activeFrames[i]++;
            if (down && !pressWas[i] && pressLogged[i] < 3)
            {
                pressLogged[i]++;
                originName.Length = 0;
                input.AAAAAAAAAAAAAAAAwf( /* GetOriginLocalizedName */ d.activeOrigin, originName, 256, -1);
                Debug.Log("[FirstPersonLoD] VR button: game action " + DigitalActions[i] + " pressed by '" + originName + "'");
            }
            pressWas[i] = down;
        }
    }

    private static void NoteOrigin(Valve.VR.CVRInput input, ulong origin, string what)
    {
        if (origin == 0) return;
        Valve.VR.InputOriginInfo_t info = new Valve.VR.InputOriginInfo_t();
        input.AAAAAAAAAAAAAAAAAAAvp( /* GetOriginTrackedDeviceInfo */ origin, ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputOriginInfo_t)));
        string cur;
        if (!originSeen.TryGetValue(info.trackedDeviceIndex, out cur)) originSeen[info.trackedDeviceIndex] = what;
        else if (cur.IndexOf(what) < 0) originSeen[info.trackedDeviceIndex] = cur + "+" + what;
    }

    private static float worstFrame, frameTimeSum;
    private static int copiesSum, perfFrames;
    private static float nextSceneCensus;
    private static string sceneCensus = "";

    private static float gpuSum, gpuMax, intervalSum, idleSum;
    private static int timingN, repCpu, repGpu, repAny, multiPresent, predictedSum, sysN;
    private static uint lastFrameIdx;
    private static double lastSysTime, sysDeltaSum, sysDeltaMax;
    private static uint droppedSum;
    private static bool timingBroken;

    private static void SampleTiming()
    {
        if (timingBroken) return;
        try
        {
            Valve.VR.CVRCompositor c = Valve.VR.OpenVR.Compositor;
            if (c == null) return;
            Valve.VR.Compositor_FrameTiming ft = new Valve.VR.Compositor_FrameTiming();
            ft.m_nSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.Compositor_FrameTiming));
            if (!c.AAAAAAAAAAAAAArv( /* GetFrameTiming */ ref ft, 0)) return;
            // one sample per compositor frame (Unity may call twice for the same one)
            if (ft.m_nFrameIndex == lastFrameIdx) return;
            if (lastSysTime > 0 && ft.m_nFrameIndex == lastFrameIdx + 1)
            {
                double d = (ft.m_flSystemTimeInSeconds - lastSysTime) * 1000.0;
                if (d > 0 && d < 1000) { sysDeltaSum += d; sysN++; if (d > sysDeltaMax) sysDeltaMax = d; }
            }
            lastFrameIdx = ft.m_nFrameIndex;
            lastSysTime = ft.m_flSystemTimeInSeconds;
            timingN++;
            idleSum += ft.m_flCompositorIdleCpuMs;
            if (ft.m_nNumFramePresents > 1) multiPresent++;
            predictedSum += (int)((ft.m_nReprojectionFlags & 0xF0) >> 4);
            gpuSum += ft.m_flTotalRenderGpuMs;
            FramePerf.GpuSample(ft.m_flTotalRenderGpuMs);
            if (ft.m_flTotalRenderGpuMs > gpuMax) gpuMax = ft.m_flTotalRenderGpuMs;
            intervalSum += ft.m_flClientFrameIntervalMs;
            droppedSum += ft.m_nNumDroppedFrames;
            if ((ft.m_nReprojectionFlags & 0x01) != 0) repCpu++;
            if ((ft.m_nReprojectionFlags & 0x02) != 0) repGpu++;
            // 0x04 only says async reprojection is AVAILABLE (always on under Link): real
            // reprojection is the Cpu/Gpu reason bits, or a frame shown more than once
            if ((ft.m_nReprojectionFlags & 0x03) != 0 || ft.m_nNumFramePresents > 1) repAny++;
        }
        catch (Exception e) { timingBroken = true; Debug.Log("[FirstPersonLoD] VR frame timing unavailable: " + Flat(e)); }
    }

    private static string TimingReport()
    {
        if (timingN == 0) return "  GPU timing: none";
        float hz = UnityEngine.VR.VRDevice.refreshRate;
        float budget = 1000f / Mathf.Max(hz, 1f);
        return "  GPU (game) avg " + (gpuSum / timingN).ToString("0.0") + " ms, worst " + gpuMax.ToString("0.0") + " ms (budget " + budget.ToString("0.0")
            + " ms @ " + hz.ToString("0") + " Hz); compositor frames " + (sysN > 0 ? (sysDeltaSum / sysN).ToString("0.0") + " ms apart (" + (1000.0 * sysN / Math.Max(sysDeltaSum, 1.0)).ToString("0") + " Hz, longest " + sysDeltaMax.ToString("0") + ")" : "?")
            + ", app WaitGetPoses interval " + (intervalSum / timingN).ToString("0.0") + " ms, compositor idle " + (idleSum / timingN).ToString("0.0")
            + " ms, dropped " + droppedSum + ", really reprojected " + (100 * repAny / timingN) + "% (cpu-late " + (100 * repCpu / timingN)
            + "%, gpu-late " + (100 * repGpu / timingN) + "%, shown twice+ " + (100 * multiPresent / timingN) + "%, extra predicted frames avg "
            + ((float)predictedSum / timingN).ToString("0.0") + "), mod " + (perfFrames > 0 ? (modMsSum / perfFrames).ToString("0.00") : "?") + " ms/frame" + Laps();
    }

    private static string Laps()
    {
        if (perfFrames == 0) return "";
        StringBuilder sb = new StringBuilder(" [");
        for (int i = 0; i < lapSum.Length; i++) sb.Append((i > 0 ? ", " : "") + LapNames[i] + " " + (lapSum[i] / perfFrames).ToString("0.00"));
        return sb.Append("]").ToString();
    }

    private static GameObject censusRoom;
    private static Renderer[] roomRenderers = new Renderer[0];
    private static long memLast, allocSum, modAllocSum;

    private static void SamplePerf()
    {
        long mem = System.GC.GetTotalMemory(false);
        if (memLast > 0 && mem > memLast) allocSum += mem - memLast;
        memLast = mem;
        SampleTiming();
        float dt = Time.unscaledDeltaTime;
        if (dt > worstFrame) worstFrame = dt;
        frameTimeSum += dt;
        perfFrames++;
        copiesSum += Sprites.CopiesDrawn;
        if (Driving && Time.realtimeSinceStartup >= nextSceneCensus)
        {
            // occasional scene census (FindObjectsOfType is slow; every 10 s only)
            nextSceneCensus = Time.realtimeSinceStartup + 10f;
            int lights = 0, pixel = 0, vis = 0, total = 0;
            Light[] ls = SceneLights.All();
            for (int i = 0; i < ls.Length; i++)
            {
                Light l = ls[i];
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                lights++;
                if (l.renderMode != LightRenderMode.ForceVertex) pixel++;
            }
            // renderers of the room you are in (collected once per room, not by a whole-scene scan)
            GameObject room = cot != null ? cot.ThisRoom : null;
            if (room != censusRoom) { censusRoom = room; roomRenderers = room != null ? room.GetComponentsInChildren<Renderer>() : new Renderer[0]; }
            for (int i = 0; i < roomRenderers.Length; i++) { if (roomRenderers[i] == null) continue; total++; if (roomRenderers[i].isVisible) vis++; }
            sceneCensus = "  scene: " + lights + " lights (" + pixel + " can be per-pixel), " + vis + "/" + total + " renderers of this room visible";
        }
    }

    private static void SampleInput()
    {
        try
        {
            frames++;
            Valve.VR.CVRInput input = Valve.VR.OpenVR.Input;
            if (input != null)
            {
                if (!handlesOk)
                {
                    input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/Move", ref hMove);
                    input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/Jump", ref hJump);
                    input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/Use", ref hUse);
                    handlesOk = true;
                }
                Valve.VR.InputAnalogActionData_t a = new Valve.VR.InputAnalogActionData_t();
                input.AAAAAAAAAAAAAAtz( /* GetAnalogActionData */ hMove, ref a,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputAnalogActionData_t)), 0);
                float rm = Mathf.Sqrt(a.x * a.x + a.y * a.y);
                if (rm > rawMoveMax) rawMoveMax = rm;
                if (rm > 0.1f) NoteOrigin(input, a.activeOrigin, "Move");

                uint dsize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputDigitalActionData_t));
                Valve.VR.InputDigitalActionData_t d = new Valve.VR.InputDigitalActionData_t();
                input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ hJump, ref d, dsize, 0);
                if (d.bState) { rawJump++; NoteOrigin(input, d.activeOrigin, "Jump"); }
                d = new Valve.VR.InputDigitalActionData_t();
                input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ hUse, ref d, dsize, 0);
                if (d.bState) { rawUse++; NoteOrigin(input, d.activeOrigin, "Use"); }
                LogPresses(input, dsize);
            }

            // what the SteamVR Unity plugin reports (same calls the game makes; obfuscated names)
            Vector2 pv = Valve.VR.SteamVR_Input.AAAAAAAAAAAAAAez( /* GetVector2 */ "Move", (Valve.VR.SteamVR_Input_Sources)0, false);
            if (pv.magnitude > plugMoveMax) plugMoveMax = pv.magnitude;
            if (Valve.VR.SteamVR_Input.AAAAAAAAAAAAAAAAAAla( /* GetState */ "Jump", (Valve.VR.SteamVR_Input_Sources)0, false)) plugJump++;
            if (Valve.VR.SteamVR_Input.AAAAAAAAAAAAAAAAAAla( /* GetState */ "Use", (Valve.VR.SteamVR_Input_Sources)0, false)) plugUse++;

            // what the game's VRInputs holds (read by menus and player 1 movement)
            VRInputs vi = VRInputs.inst;
            if (vi == null) vriNull++;
            else
            {
                if (vi.Move.magnitude > vriMoveMax) vriMoveMax = vi.Move.magnitude;
                if (vi.Jump) vriJump++;
                if (vi.Use) vriUse++;
            }
        }
        catch (Exception e)
        {
            errCount++;
            if (errCount <= 5) Debug.LogError("[FirstPersonLoD] VR input sampling: " + e);
        }
    }

    // memory every report; when the game is crawling, a census of what exists (leaks show as a
    // count that keeps growing between reports, spawn floods as one name with a huge count)
    private static float nextCensus;
    private static int lastGo, lastMat, lastMesh, lastTex;

    // what SteamVR says about the headset and this game (a game running at 3 Hz with no input is
    // SteamVR holding it in the background: headset idle or taken off, or the system menu open)
    private static string HmdState()
    {
        try
        {
            Valve.VR.CVRSystem sys = Valve.VR.OpenVR.System;
            if (sys == null) return "";
            string s = "\n  SteamVR: headset " + sys.AAAAAAAAAAAAAAAAAAAAsr(0) /* GetTrackedDeviceActivityLevel */
                + ", input " + (sys.AAAAAAAAAAAAAAAAAAry() /* IsInputAvailable */ ? "with this game" : "TAKEN by another app")
                + ", should pause " + sys.AAAAAAAAAAAAAAAAAAArb() /* ShouldApplicationPause */;
            Valve.VR.CVROverlay ov = Valve.VR.OpenVR.Overlay;
            if (ov != null) s += ", dashboard " + (ov.AAAAAAAAAAAAAAAAuj() /* IsDashboardVisible */ ? "OPEN" : "closed");
            return s;
        }
        catch (Exception e) { return "\n  SteamVR state unavailable: " + e.GetType().Name; }
    }

    private static string Memory(float fps)
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            sb.Append("memory: managed " + (System.GC.GetTotalMemory(false) / 1048576L) + " MB, Unity allocated "
                + (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576L) + " MB; sound: " + AudioGuard.Status());
            bool slow = fps > 0f && fps < 30f;
            if (!slow || Time.realtimeSinceStartup < nextCensus) return sb.ToString();
            nextCensus = Time.realtimeSinceStartup + 10f;
            int mats = Resources.FindObjectsOfTypeAll(typeof(Material)).Length;
            int meshes = Resources.FindObjectsOfTypeAll(typeof(Mesh)).Length;
            int texs = Resources.FindObjectsOfTypeAll(typeof(Texture2D)).Length;
            UnityEngine.Object[] gos = UnityEngine.Object.FindObjectsOfType(typeof(GameObject));
            Dictionary<string, int> byName = new Dictionary<string, int>();
            for (int i = 0; i < gos.Length; i++)
            {
                string n = gos[i].name;
                int c; byName.TryGetValue(n, out c); byName[n] = c + 1;
            }
            List<KeyValuePair<string, int>> top = new List<KeyValuePair<string, int>>(byName);
            top.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
            sb.Append("\n  SLOW (" + fps.ToString("0") + " fps) census: " + gos.Length + " active objects (" + (gos.Length - lastGo).ToString("+0;-0;0") + " since last census), "
                + mats + " materials (" + (mats - lastMat).ToString("+0;-0;0") + "), " + meshes + " meshes (" + (meshes - lastMesh).ToString("+0;-0;0") + "), "
                + texs + " textures (" + (texs - lastTex).ToString("+0;-0;0") + "); most common:");
            for (int i = 0; i < top.Count && i < 12; i++) sb.Append(" " + top[i].Key + " x" + top[i].Value + (i < 11 ? "," : ""));
            lastGo = gos.Length; lastMat = mats; lastMesh = meshes; lastTex = texs;
        }
        catch (Exception e) { sb.Append(" (census failed: " + e.GetType().Name + ")"); }
        return sb.ToString();
    }

    private static void LogActivity()
    {
        nextActivityLog = Time.realtimeSinceStartup + 5f;
        bool active = rawMoveMax > 0.1f || rawJump > 0 || rawUse > 0 || plugMoveMax > 0.1f || plugJump > 0
            || plugUse > 0 || vriMoveMax > 0.1f || vriJump > 0 || vriUse > 0;
        bool heartbeat = Time.realtimeSinceStartup - lastHeartbeat >= 30f;
        if (active || heartbeat || Driving)
        {
            lastHeartbeat = Time.realtimeSinceStartup;
            StringBuilder o = new StringBuilder();
            foreach (KeyValuePair<uint, string> kv in originSeen) o.Append(" device" + kv.Key + ":" + kv.Value);
            string state = "?";
            try
            {
                GameObject p1 = PlayerFinder.P1(cot);
                state = "Playing=" + cot.Playing + " Players=" + (cot.Players == null ? "null" : cot.Players.Length.ToString())
                    + " Paused=" + Kami.Inst.Paused + " P1=" + (p1 != null ? p1.name : "none") + " driving=" + Driving;
            }
            catch (Exception) { }
            float fps = perfFrames > 0 && frameTimeSum > 0f ? perfFrames / frameTimeSum : 0f;
            try
            {
                PlayerMovement2 cpm = player != null ? player.GetComponent<PlayerMovement2>() : null;
                bool conf = cpm != null && (cpm.Confusion || confusedFrames > 0);
                if (conf || wasConfused)
                    state += "  confused(drunk)=" + conf + (cpm != null ? " " + cpm.ConfusionTime.ToString("0.0") + " s left" : "")
                        + (FPConfig.VRDrunkDrift ? ", drift ON (VRDrunkDrift=true)" : ", drift suppressed for " + confusedFrames + " frames");
                wasConfused = conf;
                confusedFrames = 0;
            }
            catch (Exception) { }
            Debug.Log("[FirstPersonLoD] VR perf: " + fps.ToString("0") + " fps, worst frame " + (worstFrame * 1000f).ToString("0") + " ms, thickness copies drawn "
                + (perfFrames > 0 ? (copiesSum / perfFrames).ToString() : "0") + "/frame, sprites managed " + FPThick.Count + (Driving ? sceneCensus : "  (first person not active)")
                + "\n" + TimingReport() + "\n  " + FramePerf.Report() + (Driving ? "\n  sword: " + Sword6.Report() : "") + "\n  " + Memory(fps)
                + "\n  new garbage: game " + (frameTimeSum > 0f ? (allocSum / 1024f / frameTimeSum).ToString("0") : "?") + " KB/s, mod "
                + (frameTimeSum > 0f ? (modAllocSum / 1024f / frameTimeSum).ToString("0") : "?") + " KB/s (each garbage collection is a hitch)" + HmdState());
            Debug.Log("[FirstPersonLoD] VR input activity (" + frames + " frames): game " + state
                + "\n  OpenVR raw:     move max " + rawMoveMax.ToString("0.00") + "  Jump " + rawJump + "f  Use " + rawUse + "f  from" + (o.Length > 0 ? o.ToString() : " (none)")
                + "\n  SteamVR plugin: move max " + plugMoveMax.ToString("0.00") + "  Jump " + plugJump + "f  Use " + plugUse + "f"
                + "\n  game VRInputs:  move max " + vriMoveMax.ToString("0.00") + "  Jump " + vriJump + "f  Use " + vriUse + "f"
                + (vriNull > 0 ? "  (VRInputs.inst was NULL for " + vriNull + " frames)" : "") + ButtonActivity(frames));
        }
        worstFrame = frameTimeSum = 0f;
        copiesSum = perfFrames = 0;
        gpuSum = gpuMax = intervalSum = 0f;
        timingN = repCpu = repGpu = repAny = multiPresent = predictedSum = sysN = 0;
        idleSum = 0f; sysDeltaSum = sysDeltaMax = 0;
        droppedSum = 0;
        modMsSum = 0;
        allocSum = modAllocSum = 0;
        for (int i = 0; i < lapSum.Length; i++) lapSum[i] = 0;
        rawMoveMax = plugMoveMax = vriMoveMax = 0f;
        rawJump = rawUse = plugJump = plugUse = vriJump = vriUse = vriNull = frames = 0;
        originSeen.Clear();
    }

    private static readonly string[] DigitalActions = { "Jump", "Use", "Next", "Prev", "Hotkey1", "Hotkey2", "Menu", "Drop", "Screenshot", "TurnLeft", "TurnRight", "Recenter", "InvLeft", "InvRight", "InvUp", "InvDown", "Inventory", "Hands" };

    // OpenVR wrapper methods are called by their obfuscated names (from the shipped obfuscator log).
    // Which controllers SteamVR sees and whether each game action is bound (bActive = the action has a
    // binding on a connected controller and its action set is active). Unbound actions are the usual
    // reason "controls do nothing" on a controller the game ships no default binding for.
    private static string InputDiag()
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            Valve.VR.CVRSystem sys = Valve.VR.OpenVR.System;
            if (sys == null) return "  OpenVR system not available";
            StringBuilder prop = new StringBuilder(256);
            int found = 0;
            string ctype = "";
            for (uint i = 0; i < 64; i++)
            {
                Valve.VR.ETrackedDeviceClass c = sys.AAAAAAAAAAAAAAAAAAAAss(i) /* GetTrackedDeviceClass */;
                if (c != Valve.VR.ETrackedDeviceClass.Controller && c != Valve.VR.ETrackedDeviceClass.HMD) continue;
                Valve.VR.ETrackedPropertyError err = Valve.VR.ETrackedPropertyError.TrackedProp_Success;
                prop.Length = 0;
                sys.AAAAAAAAAAAAAAAAAAAAAqw( /* GetStringTrackedDeviceProperty */ i, Valve.VR.ETrackedDeviceProperty.Prop_ControllerType_String, prop, 256, ref err);
                string type = prop.ToString();
                if (c == Valve.VR.ETrackedDeviceClass.Controller && type.Length > 0) ctype = type;
                prop.Length = 0;
                sys.AAAAAAAAAAAAAAAAAAAAAqw( /* GetStringTrackedDeviceProperty */ i, Valve.VR.ETrackedDeviceProperty.Prop_ModelNumber_String, prop, 256, ref err);
                string model = prop.ToString();
                prop.Length = 0;
                sys.AAAAAAAAAAAAAAAAAAAAAqw( /* GetStringTrackedDeviceProperty */ i, Valve.VR.ETrackedDeviceProperty.Prop_InputProfilePath_String, prop, 256, ref err);
                sb.Append("  device " + i + ": " + c + "  controller_type=" + (type.Length > 0 ? type : "?") + "  model=" + model
                    + (prop.Length > 0 ? "  input profile=" + prop : "") + "\n");
                found++;
            }
            if (found == 0) sb.Append("  no HMD or controllers reported\n");
            if (ctype == "frame_controller")
                sb.Append("  controller layout: Steam Frame, native (mod binding bindings_frame_controller.json): D-pad left/right = previous/next item, up/down = hotkeys, left View held = recenter, left bumper held = inventory, right bumper held = hands (again = back), right Menu = pause, left trigger = free hand\n");
            else if (ctype == "oculus_touch")
                sb.Append("  controller layout: Oculus Touch (Quest / Rift; mod binding bindings_oculus_touch.json): left stick click held " + FPConfig.MenuHoldSeconds.ToString("0.0") + " s = pause, right stick click held = inventory, right grip held = hands, left trigger = switch in reach or previous item, B = next item, Y / X = hotkeys (on a Steam Frame this is SteamVR's Touch emulation: the Frame binding was not used)\n");
            else if (ctype.Length > 0) sb.Append("  controller layout: " + ctype + "\n");

            Valve.VR.CVRInput input = Valve.VR.OpenVR.Input;
            if (input == null) return sb.Append("  OpenVR input not available").ToString();
            ulong h = 0;
            Valve.VR.InputAnalogActionData_t a = new Valve.VR.InputAnalogActionData_t();
            Valve.VR.EVRInputError e1 = input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/Move", ref h);
            Valve.VR.EVRInputError e2 = input.AAAAAAAAAAAAAAtz( /* GetAnalogActionData */ h, ref a,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputAnalogActionData_t)), 0);
            sb.Append("  Move: " + (a.bActive ? "BOUND" : "UNBOUND") + " (" + e1 + "/" + e2 + ")");
            Valve.VR.InputDigitalActionData_t dd = new Valve.VR.InputDigitalActionData_t();
            uint dsize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputDigitalActionData_t));
            for (int i = 0; i < DigitalActions.Length; i++)
            {
                h = 0;
                input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/" + DigitalActions[i], ref h);
                input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ h, ref dd, dsize, 0);
                sb.Append("  " + DigitalActions[i] + ": " + (dd.bActive ? "BOUND" : "UNBOUND"));
            }
        }
        catch (Exception e) { sb.Append("  input report failed: " + Flat(e)); }
        return sb.ToString();
    }

    public static void Init()
    {
        if (inited) return;
        inited = true;
        FramePerf.Install();
        enabledFP = FPConfig.VRAuto;
        if (!FPConfig.VRHarmony)
        {
            harmonyNote = "disabled in config (VRHarmony=false)";
            Debug.Log("[FirstPersonLoD] VR: Harmony " + harmonyNote);
            return;
        }
        try
        {
            harmonyOk = PatchVRInputs();
            harmonyNote = harmonyOk ? "VRInputs.Update postfix applied" : harmonyNote;
        }
        catch (Exception e)
        {
            harmonyOk = false;
            harmonyNote = "FAILED: " + Flat(e);
        }
        Debug.Log("[FirstPersonLoD] VR: Harmony " + harmonyNote + (harmonyOk ? "" : " (locomotion will not follow head yaw)"));

        // room changes: Doors.Teleport moves the players then calls CamOTron.Teleport; stairs call
        // TeleportFloor, and the players move FadeWait seconds later in TeleEnd. Both are public.
        BindingFlags pi = BindingFlags.Public | BindingFlags.Instance;
        BindingFlags ps = BindingFlags.Public | BindingFlags.Static;
        string t1 = HarmonyShim.Postfix(typeof(CamOTron).GetMethod("Teleport", pi), typeof(VRFP).GetMethod("TeleportPostfix", ps));
        // TeleportFloor is renamed by the obfuscator in this build
        MethodInfo tf = typeof(CamOTron).GetMethod("TeleportFloor", pi) ?? typeof(CamOTron).GetMethod("AAAAAAAAAAAAAAge", pi);
        string t2 = HarmonyShim.Postfix(tf, typeof(VRFP).GetMethod("TeleportFloorPostfix", ps));
        Debug.Log("[FirstPersonLoD] VR: room-change hooks " + (t1 == null && t2 == null ? "applied" : "FAILED (" + (t1 ?? "ok") + " / " + (t2 ?? "ok") + "); teleport detection falls back to player jumps"));

        // attack lunge: the game pushes the player along world X (facing +-1); redirect it
        string t3 = HarmonyShim.Prefix(typeof(PlayerMovement2).GetMethod("Thrust", pi), typeof(VRFP).GetMethod("ThrustPrefix", ps));
        Debug.Log("[FirstPersonLoD] VR: attack lunge hook " + (t3 == null ? "applied (mode " + FPConfig.ThrustMode + ")" : t3));
        Debug.Log("[FirstPersonLoD] VR: sprite registry " + Sprites.InstallHooks());
        Debug.Log("[FirstPersonLoD] VR: projectiles " + Projectiles.Install());
        Debug.Log("[FirstPersonLoD] VR: save guard " + SaveGuard.Install());
        Debug.Log("[FirstPersonLoD] VR: stone HD " + StoneHD.Install());
        Debug.Log("[FirstPersonLoD] VR: NGUI parent-move fix " + NGUIFix.Install());
        Debug.Log("[FirstPersonLoD] Sound guard " + AudioGuard.Install());
        Debug.Log("[FirstPersonLoD] VR: torch watch " + TorchWatch.Install() + (FPConfig.TorchLog ? "" : " (logging off, TorchLog=false)"));
        BindingFlags anyI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string b1 = HarmonyShim.Prefix(typeof(Doors).GetMethod("Teleport", anyI), typeof(VRFP).GetMethod("BackTeleportPrefix", ps));
        string b2 = HarmonyShim.Prefix(typeof(Stairs).GetMethod("Teleport", anyI), typeof(VRFP).GetMethod("BackTeleportPrefix", ps));
        Debug.Log("[FirstPersonLoD] VR: bounce-back guard " + (b1 == null && b2 == null ? "applied" : "FAILED " + (b1 ?? b2)));

        // drunk/confused drift: PlayerMovement2.Update adds sin/cos wander to movement while Confusion
        MethodInfo upd = typeof(PlayerMovement2).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        string c1 = HarmonyShim.Prefix(upd, typeof(VRFP).GetMethod("PMUpdatePrefix", ps));
        string c2 = c1 == null ? HarmonyShim.Postfix(upd, typeof(VRFP).GetMethod("PMUpdatePostfix", ps)) : "skipped";
        Debug.Log("[FirstPersonLoD] VR: drunk-drift hook " + (c1 == null && c2 == null ? "applied (VRDrunkDrift=" + FPConfig.VRDrunkDrift + ")" : "FAILED " + (c1 ?? c2)));
    }

    // While first person drives, player 1's confusion keeps its timer and every other effect, but
    // Update runs with Confusion=false so the game's sin/cos walk-drift is not added. The timer is
    // counted down here exactly as the game would (ConfusionTime -= dt, ends at 0).
    private static PlayerMovement2 confusedPM;
    private static int confusedFrames;
    private static bool wasConfused;

    public static void PMUpdatePrefix(PlayerMovement2 __instance)
    {
        confusedPM = null;
        try
        {
            if (!__instance.Confusion) return;
            if (!Driving || FPConfig.VRDrunkDrift || player == null || __instance.gameObject != player) return;
            confusedFrames++;
            __instance.ConfusionTime -= Time.deltaTime;
            if (__instance.ConfusionTime <= 0f) { __instance.ConfusionTime = 0f; __instance.Confusion = false; return; }
            __instance.Confusion = false;
            confusedPM = __instance;
        }
        catch (Exception) { }
    }

    public static void PMUpdatePostfix(PlayerMovement2 __instance)
    {
        if (confusedPM != null && confusedPM == __instance) { __instance.Confusion = true; confusedPM = null; }
    }

    public static bool ThrustPrefix(PlayerMovement2 __instance, float __0)
    {
        try
        {
            if (!Driving || head == null || FPConfig.ThrustMode == "game") return true;
            if (FPConfig.ThrustMode == "off") return false;
            Vector3 f = Sword6.Active ? Sword6.BladeForward : head.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) return true;
            __instance.Shove(f.normalized * __0);
            return false;
        }
        catch (Exception) { return true; }
    }

    // door to door: players already moved when this runs
    public static void TeleportPostfix()
    {
        try
        {
            if (!Driving) return;
            lastDoorAt = Time.realtimeSinceStartup;
            needFace = true; faceWhy = "door";
        }
        catch (Exception) { }
    }

    // stairs: the game fades out over FadeWait; players move in TeleEnd afterwards
    public static void TeleportFloorPostfix()
    {
        try
        {
            if (!Driving) return;
            lastStairsAt = Time.realtimeSinceStartup;
            faceWhy = "stairs";
        }
        catch (Exception) { }
    }

    // OpenVR compositor fade (CVRCompositor.FadeToColor, obfuscated name from the shipped log)
    private static void CompositorFade(float seconds, float alpha)
    {
        try
        {
            Valve.VR.CVRCompositor c = Valve.VR.OpenVR.Compositor;
            if (c != null) c.AAAAAAAAAAAAAAAsn( /* FadeToColor */ seconds, 0f, 0f, 0f, alpha, false);
        }
        catch (Exception e) { if (errCount++ < 5) Debug.LogError("[FirstPersonLoD] VR fade: " + Flat(e)); }
    }

    private static void Mirror(float a, float seconds)
    {
        if (Mathf.Abs(a - mirrorAlpha) < 0.02f && !(a <= 0f && mirrorAlpha > 0f)) return;
        mirrorAlpha = a;
        compositorTouched = true;
        CompositorFade(seconds, a);
    }

    // The game has ONE transition darkness: CamOTron.BlackDrop, an NGUI black drop placed for the
    // tabletop view. Doors: the players are moved first, then the game fades to black over FadeWait
    // and back (hiding its tabletop camera moving to the new room). In first person the view is
    // already in the new room, so that fade is just a blink after arrival: skipped by default.
    // Stairs and loading: the fade hides the floor change, so it is shown in the headset with the
    // game's own timing. The drop itself is hidden from the world while first person drives, so
    // there is never a second, separate darkness on top.
    private static void GameFadeTick()
    {
        TweenAlpha bd = cot.BlackDrop;
        GameObject g = bd != null ? bd.gameObject : null;
        if (g != examinedDrop) { UnhideDrop(); HideDrop(g); examinedDrop = g; }
        if (hiddenDrop != null && hiddenDrop.activeSelf) { dropWanted = true; hiddenDrop.SetActive(false); }

        float now = Time.realtimeSinceStartup;
        bool visibleInGame = bd != null && (hiddenDrop == null ? g.activeInHierarchy
            : dropWanted && (g.transform.parent == null || g.transform.parent.gameObject.activeInHierarchy));
        float a = visibleInGame ? Mathf.Clamp01(bd.alpha) : 0f;

        if (a > 0.005f)
        {
            if (fadeKind.Length == 0)
            {
                fadeKind = now - lastStairsAt < 1.5f ? "stairs" : now - lastDoorAt < 1.5f ? "door" : "other";
                fadeStart = now; fadePeak = 0f; fadeCut = false;
                fadeShown = fadeKind == "door" ? FPConfig.DarkenDoors : fadeKind == "stairs" ? FPConfig.DarkenStairs : FPConfig.DarkenLoading;
            }
            fadePeak = Mathf.Max(fadePeak, a);
            if (fadeShown && !fadeCut && now - fadeStart > FPConfig.MaxDarkSeconds)
            {
                fadeCut = true;
                Debug.Log("[FirstPersonLoD] VR transition (" + fadeKind + "): the game has held its black drop for "
                    + FPConfig.MaxDarkSeconds.ToString("0.#") + " s (alpha " + a.ToString("0.00") + "); headset view brought back");
            }
            bool show = fadeShown && !fadeCut;
            Mirror(show ? a : 0f, show ? 0f : FPConfig.FadeSeconds);
        }
        else
        {
            if (fadeKind.Length > 0)
            {
                Debug.Log("[FirstPersonLoD] VR transition (" + fadeKind + "): game fade " + (now - fadeStart).ToString("0.00")
                    + " s, peak " + fadePeak.ToString("0.00") + ", FadeWait " + cot.FadeWait.ToString("0.00")
                    + ", in headset: " + (fadeShown ? (fadeCut ? "cut short" : "shown") : "skipped (you walk straight through)")
                    + "; fog start " + RenderSettings.fogStartDistance.ToString("0.#"));
                fadeKind = "";
            }
            Mirror(0f, 0f);
        }
    }

    private static bool IsOrContains(GameObject g, GameObject other)
    {
        return other != null && (other == g || other.transform.IsChildOf(g.transform));
    }

    private static void HideDrop(GameObject g)
    {
        hiddenDrop = null;
        if (g == null) { dropNote = "none"; return; }
        StringBuilder sb = new StringBuilder();
        for (Transform x = g.transform; x != null; x = x.parent) sb.Insert(0, "/" + x.name);
        Component[] comps = g.GetComponents<Component>();
        StringBuilder cs = new StringBuilder();
        for (int i = 0; i < comps.Length; i++) if (comps[i] != null) cs.Append(" " + comps[i].GetType().Name);
        int widgets = g.GetComponentsInChildren<UIWidget>(true).Length;
        float dist = head != null ? (g.transform.position - head.position).magnitude / Mathf.Max(worldScale, 0.0001f) : -1f;
        string where = sb + " (layer " + g.layer + ", active " + g.activeSelf + ", alpha " + cot.BlackDrop.alpha.ToString("0.00")
            + ", " + widgets + " widgets, components" + cs + ", " + (dist >= 0f ? dist.ToString("0.0") + " m from your eyes" : "?")
            + (IsOrContains(g, cot.p1gui) ? ", contains the HUD" : "") + ")";
        bool unsafeToHide = IsOrContains(g, cot.p1gui) || IsOrContains(g, cot.UIRoot) || IsOrContains(g, cot.Loadingstuff)
            || IsOrContains(g, cot.MainMenu) || IsOrContains(g, cot.Scoreboard) || IsOrContains(g, cot.ArcadeNames)
            || IsOrContains(g, cot.VRRig) || widgets > 4;
        if (!FPConfig.HideGameDrop || unsafeToHide)
        {
            dropNote = where + (unsafeToHide ? "; shares an object with other UI, left in place" : "; left in place (HideGameDrop=false)");
        }
        else
        {
            dropWanted = g.activeSelf;
            if (g.activeSelf) g.SetActive(false);
            hiddenDrop = g;
            dropNote = where + "; hidden from the world while first person drives";
        }
        Debug.Log("[FirstPersonLoD] VR game black drop: " + dropNote);
    }

    private static void UnhideDrop()
    {
        examinedDrop = null;
        if (hiddenDrop != null && dropWanted && !hiddenDrop.activeSelf) hiddenDrop.SetActive(true);
        hiddenDrop = null;
        dropWanted = false;
    }

    private static string Flat(Exception e)
    {
        while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
        return e.GetType().Name + ": " + e.Message;
    }

    private static void Say(string m)
    {
        Msg = "[FirstPersonLoD] " + m;
        MsgUntil = Time.realtimeSinceStartup + 3f;
        Debug.Log(Msg);
    }
    public static void SayPublic(string m) { Say(m); }

    // Live tuning of the first-person stone (number pad; F7 saves to the config file):
    //   - / +   rough masonry strength (BrickRough) 0 .. 2.5 in steps of 0.25; rooms are rebuilt
    //   / / *   HD stone detail (StoneHDStrength) 0 .. 2 in steps of 0.25; textures are rebuilt
    //   F1      HD stone / the game's textures (in StoneHD)
    // A change waits 0.6 s for more presses before rebuilding.
    private static float roughRebuildAt = -1f, detailRebuildAt = -1f, pendingRough = -1f;
    private static void TuneKeys()
    {
        float now = Time.realtimeSinceStartup;
        if (Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.KeypadPlus))
        {
            float st = Input.GetKeyDown(KeyCode.KeypadMinus) ? -0.25f : 0.25f;
            float cur = pendingRough >= 0f ? pendingRough : FPConfig.BrickRough;
            pendingRough = Mathf.Clamp(Mathf.Round((cur + st) * 4f) / 4f, 0f, 2.5f);
            roughRebuildAt = now + 0.6f;
            Say("Rough masonry " + pendingRough.ToString("0.00") + (pendingRough == 0f ? " (the game's straight bricks)" : "") + " (F7 saves)");
        }
        if (Input.GetKeyDown(KeyCode.KeypadDivide) || Input.GetKeyDown(KeyCode.KeypadMultiply))
        {
            float st = Input.GetKeyDown(KeyCode.KeypadDivide) ? -0.25f : 0.25f;
            FPConfig.StoneHDStrength = Mathf.Clamp(Mathf.Round((FPConfig.StoneHDStrength + st) * 4f) / 4f, 0f, 2f);
            detailRebuildAt = now + 0.6f;
            Say("HD stone detail " + FPConfig.StoneHDStrength.ToString("0.00") + " (F7 saves)");
        }
        if (roughRebuildAt > 0f && now >= roughRebuildAt)
        {
            roughRebuildAt = -1f;
            if (pendingRough >= 0f) FPConfig.BrickRough = pendingRough;
            pendingRough = -1f;
            BrickBatch.Rebuild(cot != null ? cot.ThisRoom : null);
            Debug.Log("[FirstPersonLoD] VR bricks: rebuilding every room at rough masonry " + FPConfig.BrickRough.ToString("0.00"));
        }
        if (detailRebuildAt > 0f && now >= detailRebuildAt)
        {
            detailRebuildAt = -1f;
            StoneHD.Rebuild();
            Debug.Log("[FirstPersonLoD] VR stone HD: rebuilding the HD textures at detail " + FPConfig.StoneHDStrength.ToString("0.00"));
        }
    }

    private static bool PatchVRInputs()
    {
        MethodInfo target = typeof(VRInputs).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (target == null) { harmonyNote = "VRInputs.Update not found"; return false; }
        string err = HarmonyShim.Postfix(target, typeof(VRFP).GetMethod("VRInputsPostfix", BindingFlags.Static | BindingFlags.Public));
        if (err != null) { harmonyNote = err; return false; }
        return true;
    }

    // The pause menu from a thumbstick click (Touch, Index; on the Frame it is the right Menu button
    // and passes untouched): the click counts only when held MenuHoldSeconds with the stick near
    // the middle. Pushing the stick hard while running presses it without meaning to; that paused
    // the game three times in one session (v0.9.6 log: "Menu pressed by Left Hand ... Thumb Stick").
    private static ulong hMenuH;
    private static bool menuDown, menuFired;
    private static float menuT, menuStick;
    private static int menuLogs;
    private static void MenuGuard(VRInputs vi)
    {
        if (FPConfig.MenuHoldSeconds <= 0f || OffHandUse.IsFrame) return;
        bool held;
        if (!ReadHeld("Menu", ref hMenuH, out held)) return;
        float stick = vi.Move.magnitude;
        if (vi.Menu) { vi.Menu = false; if (!menuDown) { menuDown = true; menuFired = false; menuT = 0f; menuStick = stick; } }
        if (!menuDown) return;
        if (held)
        {
            menuT += Time.unscaledDeltaTime;
            menuStick = Mathf.Max(menuStick, stick);
            if (!menuFired && menuT >= FPConfig.MenuHoldSeconds)
            {
                menuFired = true;
                if (menuStick < 0.5f) { vi.Menu = true; if (menuLogs++ < 30) Debug.Log("[FirstPersonLoD] VR pause: stick click held " + menuT.ToString("0.0") + " s"); }
                else if (menuLogs++ < 30) Debug.Log("[FirstPersonLoD] VR pause: stick click ignored (stick pushed " + menuStick.ToString("0.00") + " while clicking: a click while running, not a pause)");
            }
        }
        else
        {
            if (!menuFired && menuLogs++ < 30) Debug.Log("[FirstPersonLoD] VR pause: stick click ignored (let go after " + menuT.ToString("0.00") + " s; hold " + FPConfig.MenuHoldSeconds.ToString("0.0") + " s to pause)");
            menuDown = false;
        }
    }

    // Runs right after VRInputs.Update, before PlayerMovement2 consumes Move. The game maps
    // Move.x to world X and Move.y to world depth (+Z), so rotate the stick by head yaw.
    public static void VRInputsPostfix()
    {
        try
        {
            if (VRInputs.inst == null) return;
            // the free hand's trigger: a switch in reach takes the press (before the D-pad mapping,
            // so the D-pad's own "previous" is not taken away)
            if (Driving) OffHandUse.Filter(VRInputs.inst, head);
            // pause from a stick click only when meant (held, stick centred)
            if (Driving) MenuGuard(VRInputs.inst);
            if (Driving && VRInputs.inst.Drop) PickupMagnet.DropPressed();
            // two hands on a gun: the free hand's grip (drop) would throw the gun away
            if (Driving && Sword6.TwoHanded && VRInputs.inst.Drop) VRInputs.inst.Drop = false;
            // D-pad -> previous/next item and hotkeys; everything blocked while the inventory is open
            InvMenu.Filter(VRInputs.inst);
            if (InvMenu.Blocking) return;
            if (!Driving || head == null) return;
            // physical swing -> the game's own attack: press this frame, release the next
            // (the game charges on press and swings on release)
            Sword6.RealUseHeld = FPConfig.TriggerMode == "release" && (VRInputs.inst.Use || VRInputs.inst.UseDN);
            bool realPress = VRInputs.inst.UseDN;
            if (Sword6.Inject == 2) { VRInputs.inst.UseDN = true; Sword6.Inject = 1; }
            else if (Sword6.Inject == 1) { VRInputs.inst.UseUP = true; Sword6.Inject = 0; }
            // press mode: the game charges on press and strikes on release; release for it next frame
            if (realPress && FPConfig.TriggerMode == "press" && Sword6.Inject == 0) Sword6.Inject = 1;
            Vector2 m = VRInputs.inst.Move;
            if (FPConfig.SnapTurnStick == "left")
            {
                // left-stick flick left/right turns; its sideways axis no longer strafes
                if (!leftFlickArmed && Mathf.Abs(m.x) < 0.3f) leftFlickArmed = true;
                if (leftFlickArmed && Mathf.Abs(m.x) > 0.75f)
                {
                    leftFlickArmed = false;
                    SnapTurn(m.x > 0f ? FPConfig.SnapTurn : -FPConfig.SnapTurn);
                }
                m.x = 0f;
                VRInputs.inst.Move = m;
            }
            if (m.sqrMagnitude < 0.0001f) return;
            // stick is in head space (x right, y forward); convert to world X/Z by head yaw
            float yaw = head.eulerAngles.y * Mathf.Deg2Rad;
            float wx = m.x * Mathf.Cos(yaw) + m.y * Mathf.Sin(yaw);
            float wz = -m.x * Mathf.Sin(yaw) + m.y * Mathf.Cos(yaw);
            // the game turns Move.y into world depth; FlipDepth covers the case where that sign is reversed
            VRInputs.inst.Move = new Vector2(wx, FPConfig.FlipDepth ? -wz : wz);
        }
        catch (Exception) { }
    }

    private static GameObject ActivePlayer() { return PlayerFinder.P1(cot); }

    private static readonly System.Diagnostics.Stopwatch modWatch = new System.Diagnostics.Stopwatch();
    private static double modMsSum;
    // per-section cost: 0 input+rig, 1 sword, 2 body/weapon view/HUD, 3 lights, 4 sprites
    private static readonly double[] lapSum = new double[7];
    private static double lapPrev;
    private static readonly string[] LapNames = { "input+rig", "sword", "body+hud", "lights", "sprites", "magnet", "rooms" };
    private static void Lap(int i)
    {
        double now = modWatch.Elapsed.TotalMilliseconds;
        lapSum[i] += now - lapPrev;
        lapPrev = now;
    }

    public static double LastTickMs;
    public static string CurrentRoomName { get { return cot != null && cot.ThisRoom != null ? cot.ThisRoom.name : null; } }
    public static GameObject CurrentRoom { get { return cot != null ? cot.ThisRoom : null; } }
    public static void Tick()
    {
        modWatch.Reset(); modWatch.Start();
        lapPrev = 0;
        long m0 = System.GC.GetTotalMemory(false);
        try { TickInner(); }
        catch (Exception e)
        {
            errCount++;
            if (errCount <= 5) Debug.LogError("[FirstPersonLoD] VR: " + e);
        }
        long m1 = System.GC.GetTotalMemory(false);
        if (m1 > m0) modAllocSum += m1 - m0;
        memLast = m1;   // the mod's own garbage is counted here, not again in the frame total
        modWatch.Stop();
        LastTickMs = modWatch.Elapsed.TotalMilliseconds;
        modMsSum += LastTickMs;
    }

    private static void TickInner()
    {
        InvMenu.Watchdog();
        cot = (Kami.Inst != null) ? Kami.Inst.thecamotron : null;
        if (cot == null || !cot.VR) { Release("not in VR"); return; }

        SampleInput();
        SamplePerf();
        if (Time.realtimeSinceStartup >= nextActivityLog) LogActivity();

        if (Time.realtimeSinceStartup >= nextInputDiag)
        {
            nextInputDiag = Time.realtimeSinceStartup + 5f;
            string d = InputDiag();
            if (d != lastInputDiag)
            {
                lastInputDiag = d;
                Debug.Log("[FirstPersonLoD] VR input report:\n" + d);
            }
        }

        // view toggle: first person <-> the game's own tabletop view (everything the mod changes is
        // handed back while off, so the tabletop is the vanilla game for side-by-side comparison)
        // controller: hold A (jump) + left trigger (previous item) for 2 s. Matches B + left trigger
        // (give all items); B held means that combo, so it never counts here.
        bool lt, ra, rb;
        bool padToggle = false;
        if (!InvMenu.Blocking && ReadHeld("Prev", ref hPrevH, out lt) && lt && ReadHeld("Jump", ref hJumpH, out ra) && ra
            && !(ReadHeld("Next", ref hNextH, out rb) && rb))
        {
            if (toggleHeld == 0f) Debug.Log("[FirstPersonLoD] VR: A + left trigger held (view switch after " + FPConfig.ToggleHoldSeconds.ToString("0.#") + " s)");
            toggleHeld += Time.unscaledDeltaTime;
            if (toggleHeld >= FPConfig.ToggleHoldSeconds) { toggleHeld = -999f; padToggle = true; }
        }
        else toggleHeld = 0f;
        bool keyToggle = Input.GetKeyDown(FPConfig.ToggleKey) || (Input.GetKeyDown(KeyCode.F2) && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift));
        if (padToggle || keyToggle)
        {
            enabledFP = !enabledFP;
            Say("VR view: " + (enabledFP ? "first person" : "game's tabletop view") + (padToggle ? " (A + left trigger held)" : " (keyboard)"));
            if (enabledFP) needRecenter = true;
        }

        GameObject prevPlayer = player;
        player = ActivePlayer();
        if (player != prevPlayer && player != null)
            Debug.Log("[FirstPersonLoD] VR following " + player.name + " (tag " + player.tag + ")" + (PlayerFinder.Note.Length > 0 ? ": " + PlayerFinder.Note : ""));
        bool paused = Kami.Inst.Paused;
        if (!enabledFP || !cot.Playing || paused || player == null)
        {
            string why = !enabledFP ? "toggled off (F6)" : paused ? "paused" : !cot.Playing ? "not playing" : "no player found";
            bool changed = why != lastIdleReason;
            if (changed)
            {
                lastIdleReason = why;
                Debug.Log("[FirstPersonLoD] VR first person idle: " + why);
            }
            Release(why);
            FramePerf.ProbeTick(false);
            StoneHD.Tick(false);
            GarbageProbe.Tick(false, null);
            UIProbe.Tick(false, null);
            // the game's tabletop view (first person toggled off while playing): the inventory screen
            // and the give-all combo work here too
            if (!enabledFP && cot.Playing && !paused && player != null)
            {
                Transform th = TabletopHead();
                if (th != null) InvMenu.Tick(player, th); else InvMenu.Close("inventory closed: no headset camera in the tabletop view");
                GiveAllCombo();
            }
            else InvMenu.Close("inventory closed: " + why);
            // menus (pause, game over, loading) are placed by the game for its calibrated head
            // position; re-centre on where your head is now, like the game's own Reorient option
            // (only once the headset is really tracked: at game start and after a scene load the head
            // reads 0,0,0 for a moment, and centring on that dropped every menu far below you)
            // The tabletop view (toggled off, playing) gets the same centring plus the diorama placed
            // DioramaBelowEye under your eyes, so you look down onto it like a model on a table.
            string view = paused || !cot.Playing ? "menu" : !enabledFP ? "tabletop" : "";
            if (view != lastPlacedView)
            {
                lastPlacedView = view;
                bool want = view == "menu" ? FPConfig.MenuRecenter : view == "tabletop" ? FPConfig.MenuRecenter || FPConfig.DioramaBelowEye > 0f : false;
                if (want) { pendingMenuRecenter = view == "menu" ? (paused ? "paused" : "not playing") : "tabletop view"; pendingSince = Time.realtimeSinceStartup; trackedSince = -1f; }
                else pendingMenuRecenter = "";
            }
            if (pendingMenuRecenter.Length > 0) TryMenuRecenter();
            return;
        }
        lastIdleReason = "";
        pendingMenuRecenter = "";
        lastPlacedView = "";
        bool fresh = !detached;
        if (!Acquire()) return;
        if (fresh)
        {
            // clear any compositor fade left from before (nothing of the mod's may stay dark)
            CompositorFade(0f, 0f);
            mirrorAlpha = 0f;
            fadeKind = "";
            if (FPConfig.WarmupShaders && !warmedUp)
            {
                warmedUp = true;
                System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                Shader.WarmupAllShaders();
                Debug.Log("[FirstPersonLoD] VR shaders warmed up in " + w.ElapsedMilliseconds + " ms (every loaded shader variant compiled now instead of mid-game)");
            }
            Debug.Log("[FirstPersonLoD] VR render state at engage: " + VRPerf.State(headCam));
            needFace = true;
            // back from a pause in the same room: keep the direction you were facing
            faceWhy = hasSavedYaw && savedRoom != null && savedRoom == cot.ThisRoom ? "resume" : "start";
            lastPosValid = false;
            lastRoom = cot.ThisRoom;
            RoomShape.Show(true);
            BrickBatch.Show(true);
            RoomShape.Room(cot.ThisRoom);
            Sprites.SetRoom(cot.ThisRoom);
            BrickBatch.Room(cot.ThisRoom);
        }

        HandleKeys();
        InvMenu.Tick(player, head);
        Lap(0);
        if (!InvMenu.Blocking) PickupMagnet.Tick(player);
        Lap(5);
        BrickBatch.Tick();
        StoneHD.Tick(true);
        Lap(6);
        TuneKeys();
        PlayerMovement2 pm = player.GetComponent<PlayerMovement2>();

        // teleport detection: any single-frame jump of the player (doors, stairs, respawn)
        Vector3 pp = player.transform.position;
        if (lastPosValid && (pp - lastPlayerPos).sqrMagnitude > FPConfig.JumpDist * FPConfig.JumpDist)
        {
            needFace = true;
            if (faceWhy.Length == 0) faceWhy = "jump";
            if (faceWhy == "stairs" || !probedOnce) { probedOnce = true; FramePerf.ArmProbe(faceWhy == "stairs" ? "new floor" : "first room"); }
            Debug.Log("[FirstPersonLoD] VR teleport seen (" + faceWhy + "): moved " + (pp - lastPlayerPos).magnitude.ToString("0.0")
                + " units, room " + (cot.ThisRoom != null ? cot.ThisRoom.name : "?"));
        }
        lastPlayerPos = pp;
        lastPosValid = true;
        if (cot.ThisRoom != lastRoom)
        {
            lastRoom = cot.ThisRoom;
            LogRoom();
            RoomShape.Room(cot.ThisRoom);
            Sprites.SetRoom(cot.ThisRoom);
            BrickBatch.Room(cot.ThisRoom);
        }

        // follow first, so recenter and turning pivot around where the head is now (after a teleport
        // the head is still in the previous room until this runs)
        root.position = pp + rootOffset;
        if (needRecenter) Recenter();
        if (needFace)
        {
            needFace = false;
            if (faceWhy == "resume") FaceYaw(savedYaw, "resume", "same as before the pause");
            else FaceRoom(faceWhy);
            faceWhy = "";
        }
        Leash();
        GameFadeTick();
        DoorWatch(pp);

        GameObject held = pm != null ? pm.HeldItem : null;
        Lap(0);
        AimAtHead();
        bool inHand = Sword6.Tick(held, head, worldScale) || FPConfig.Weapon6DOF;
        Lap(1);
        // the real weapon stays visible only when the flat fallback view is used
        if (FPConfig.HideOwnBody) FPDriver.HideBody(player, inHand ? null : held); else FPDriver.RestoreBody();
        if (inHand) WeaponView.Release(); else WeaponView.Tick(held, head, worldScale);
        VRHands.Tick(head, worldScale);
        OffHand.Tick(player, head, held, inHand);
        VRHud.Tick(cot, head, headCam, worldScale);
        Lap(2);
        VRPerf.Tick(pp, headCam, head, worldScale);
        VRPerf.TickThrottled();
        FramePerf.HeadCam = headCam;
        FramePerf.ResetCollect();
        FramePerf.ProbeTick(true);
        GarbageProbe.Tick(true, CurrentRoomName);
        UIProbe.Tick(true, CurrentRoomName);
        Lap(3);
        FPDriver.Billboards(head.position, player);
        Lap(4);
        Driving = true;
    }

    // ---- body: eye height from the collision capsule ----
    private static void MeasureBody()
    {
        PlayerMovement2 pm = player.GetComponent<PlayerMovement2>();
        CharacterController cc = pm != null && pm.Con != null ? pm.Con : player.GetComponent<CharacterController>();
        StringBuilder sb = new StringBuilder();
        float py = player.transform.position.y;
        if (cc != null && FPConfig.EyeFrac > 0f)
        {
            Vector3 s = cc.transform.lossyScale;
            float sy = Mathf.Abs(s.y), sxz = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            float r = cc.radius * sxz;
            float h = Mathf.Max(cc.height * sy, 2f * r);
            float cy = cc.transform.TransformPoint(cc.center).y;
            float feet = cy - h * 0.5f, top = cy + h * 0.5f;
            eyeOffset = feet + h * FPConfig.EyeFrac - py;
            bodyRadius = r;
            sb.Append("capsule height " + h.ToString("0.000") + " radius " + r.ToString("0.000") + " feet " + (feet - py).ToString("0.000")
                + " top " + (top - py).ToString("0.000") + " (relative to player origin) -> eye " + eyeOffset.ToString("0.000")
                + " (" + (FPConfig.EyeFrac * 100f).ToString("0") + "% of body)");
        }
        else
        {
            eyeOffset = FPConfig.EyeHeight;
            bodyRadius = 0.1f;
            sb.Append(cc == null ? "no CharacterController; " : "EyeFrac=0; ");
            sb.Append("fixed eye " + eyeOffset.ToString("0.000"));
        }
        // sprite size for comparison (the quad that was hidden)
        Animate a = player.GetComponentInChildren<Animate>();
        Renderer ar = a != null ? a.GetComponent<Renderer>() : null;
        if (ar != null)
            sb.Append("  | sprite y " + (ar.bounds.min.y - py).ToString("0.000") + ".." + (ar.bounds.max.y - py).ToString("0.000")
                + " width " + ar.bounds.size.x.ToString("0.000"));
        bodyNote = sb.ToString();
    }

    // ---- facing: turn toward the room's middle (doors sit on the back wall) ----
    private static bool RoomCenter(GameObject room, out Vector3 c, out string how)
    {
        c = Vector3.zero; how = "none";
        if (room == null) return false;
        Bounds b = new Bounds();
        bool any = false;
        Collider[] cols = room.GetComponentsInChildren<Collider>();
        for (int i = 0; i < cols.Length; i++)
        {
            Collider k = cols[i];
            if (k == null || k.isTrigger || k.attachedRigidbody != null || k is CharacterController) continue;
            if (!any) { b = k.bounds; any = true; } else b.Encapsulate(k.bounds);
        }
        if (any) how = "colliders";
        else
        {
            Renderer[] rs = room.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                if (!any) { b = rs[i].bounds; any = true; } else b.Encapsulate(rs[i].bounds);
            }
            if (any) how = "renderers";
        }
        if (!any) return false;
        c = b.center;
        how += " " + b.min.ToString("F1") + ".." + b.max.ToString("F1");
        return true;
    }

    // Arrival through a door or stairs, derived from the arrival door's own trigger box (the game
    // records it as Kami.theLastDoor for both kinds). The game sends you through when your position
    // is inside a door's box; it drops you inside the other door's box, and walking back into it
    // (after a 1 s grace) sends you back. Every box reaches from its door into the wall, so the way
    // out is from the box centre toward the door's own position. On arrival: step you just past
    // that edge through the game's collision, and face that way, so holding forward (which is what
    // got you through) keeps carrying you away from the door and into the next space.
    // Safety net: the door or stairs you just came out of cannot send you back for BounceGuard
    // seconds after arrival (the game itself only waits 1 s after you leave its box).
    private static GameObject arrivedVia;
    private static float arrivedAt = -100f;
    private static int bouncesBlocked;

    public static bool BackTeleportPrefix(Component __instance)
    {
        try
        {
            if (!Driving || FPConfig.BounceGuard <= 0f || __instance == null || arrivedVia == null || __instance.gameObject != arrivedVia) return true;
            if (Time.realtimeSinceStartup - arrivedAt > FPConfig.BounceGuard) return true;
            bouncesBlocked++;
            if (bouncesBlocked <= 20)
                Debug.Log("[FirstPersonLoD] VR blocked an immediate bounce back through " + arrivedVia.name + " ("
                    + (Time.realtimeSinceStartup - arrivedAt).ToString("0.0") + " s after arriving)");
            return false;
        }
        catch (Exception) { return true; }
    }

    private static bool ExitArrivalDoor(CharacterController cc, out Vector3 exit, out string note)
    {
        exit = Vector3.back;
        note = "";
        GameObject d = Kami.Inst != null ? Kami.Inst.theLastDoor : null;
        BoxCollider bc = d != null ? d.GetComponent<BoxCollider>() : null;
        Vector3 pp = player.transform.position;
        if (bc == null)
        {
            // fall back to any door/stairs box around you
            MonoBehaviour[] cands = cot.ThisRoom != null ? cot.ThisRoom.GetComponentsInChildren<MonoBehaviour>() : new MonoBehaviour[0];
            for (int i = 0; i < cands.Length && bc == null; i++)
            {
                if (!(cands[i] is Doors) && !(cands[i] is Stairs)) continue;
                BoxCollider c = cands[i].GetComponent<BoxCollider>();
                if (c != null && c.bounds.Contains(pp)) { bc = c; d = cands[i].gameObject; }
            }
        }
        if (bc == null) { note = "arrival door unknown"; return false; }
        Bounds bx = bc.bounds;
        Vector3 off = d.transform.position - bx.center; off.y = 0f;
        if (off.magnitude > 0.05f)
            exit = Mathf.Abs(off.x) > Mathf.Abs(off.z) ? new Vector3(Mathf.Sign(off.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(off.z));
        else
        {
            // door centred in its box: leave toward the room's middle along the box's short side
            Vector3 c; string h;
            Vector3 to = RoomCenter(cot.ThisRoom, out c, out h) ? c - bx.center : Vector3.back;
            exit = bx.size.x < bx.size.z ? new Vector3(Mathf.Sign(to.x == 0f ? 1f : to.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(to.z == 0f ? -1f : to.z));
        }
        // candidate ways out: the geometric one first, then toward the room's middle, then the rest;
        // doors pull you onto their centre line while you touch them, so doors only leave along Z
        bool isDoor = d.GetComponent<Doors>() != null;
        Vector3 rc; string rh;
        Vector3 toMid = RoomCenter(cot.ThisRoom, out rc, out rh) ? rc - bx.center : Vector3.back;
        List<Vector3> ways = new List<Vector3>();
        ways.Add(exit);
        Vector3 zMid = new Vector3(0f, 0f, toMid.z >= 0f ? 1f : -1f), xMid = new Vector3(toMid.x >= 0f ? 1f : -1f, 0f, 0f);
        Vector3[] more = { zMid, xMid, -zMid, -xMid };
        for (int i = 0; i < more.Length; i++) if (!ways.Contains(more[i]) && !(isDoor && more[i].x != 0f)) ways.Add(more[i]);

        Vector3 origin = cc != null ? cc.transform.TransformPoint(cc.center) : pp;
        float rad = Mathf.Max(bodyRadius * 0.9f, 0.02f);
        StringBuilder tried = new StringBuilder();
        Vector3 before = pp;
        // pick: first candidate whose path is free far enough; else the one with the most room
        Vector3 pick = ways[0];
        float pickFree = -1f, pickNeed = 0f;
        for (int i = 0; i < ways.Count; i++)
        {
            Vector3 c = ways[i];
            float need = EdgeDistance(bx, pp, c) + FPConfig.DoorExitMargin;
            float free = FreeDistance(origin, rad, c, need + 0.5f);
            tried.Append(" " + c.ToString("F0") + " needs " + need.ToString("0.00") + " free " + free.ToString("0.00") + ";");
            if (free >= need) { pick = c; pickFree = free; pickNeed = need; break; }
            if (free > pickFree) { pick = c; pickFree = free; pickNeed = need; }
        }
        exit = pick;
        bool moved = false;
        if (cc != null && cc.enabled)
        {
            cc.Move(exit * pickNeed);
            moved = true;
            // still inside (blocked)? try the other candidates from where we are
            for (int i = 0; i < ways.Count && bx.Contains(player.transform.position); i++)
            {
                if (ways[i] == exit) continue;
                float need = EdgeDistance(bx, player.transform.position, ways[i]) + FPConfig.DoorExitMargin;
                cc.Move(ways[i] * need);
                if (!bx.Contains(player.transform.position)) { exit = ways[i]; tried.Append(" retried " + ways[i].ToString("F0") + ";"); }
            }
        }
        Vector3 after = player.transform.position;
        note = d.name + " (" + (isDoor ? "door" : "stairs") + ") box " + bx.min.ToString("F2") + ".." + bx.max.ToString("F2")
            + ", door at " + d.transform.position.ToString("F2") + " -> way out " + exit.ToString("F0") + "; tried" + tried
            + " moved " + (after - before).magnitude.ToString("0.00") + (moved ? "" : " (no controller)")
            + ", now " + (bx.Contains(after) ? "STILL INSIDE the box" : "clear of the box");
        return true;
    }

    private static float EdgeDistance(Bounds bx, Vector3 p, Vector3 dir)
    {
        float e = dir.x > 0f ? bx.max.x - p.x : dir.x < 0f ? p.x - bx.min.x : dir.z > 0f ? bx.max.z - p.z : p.z - bx.min.z;
        return Mathf.Max(0f, e);
    }

    // open distance along dir for a sphere the size of the body, ignoring your own character,
    // anything that moves (pets, enemies), triggers, and anything already overlapping at the start
    private static float FreeDistance(Vector3 origin, float radius, Vector3 dir, float max)
    {
        RaycastHit[] hits = Physics.SphereCastAll(origin, radius, dir, max, ~0, QueryTriggerInteraction.Ignore);
        float free = max;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider c = hits[i].collider;
            if (c == null || c.attachedRigidbody != null || c is CharacterController) continue;
            if (player != null && c.transform.IsChildOf(player.transform)) continue;
            if (hits[i].distance <= 0f) continue;
            if (hits[i].distance < free) free = hits[i].distance;
        }
        return free;
    }

    private static void FaceRoom(string why)
    {
        if (!FPConfig.AutoFaceRoom || head == null || root == null) return;
        PlayerMovement2 pm = player.GetComponent<PlayerMovement2>();
        CharacterController cc = pm != null && pm.Con != null ? pm.Con : player.GetComponent<CharacterController>();
        if (why == "door" || why == "stairs")
        {
            arrivedVia = Kami.Inst != null ? Kami.Inst.theLastDoor : null;
            arrivedAt = Time.realtimeSinceStartup;
            Vector3 exit; string n;
            bool ok = ExitArrivalDoor(cc, out exit, out n);
            root.position = player.transform.position + rootOffset; // the view comes along
            lastPlayerPos = player.transform.position;
            FaceYaw(Mathf.Atan2(exit.x, exit.z) * Mathf.Rad2Deg, why, (ok ? "out of " : "") + n);
            return;
        }
        Vector3 pp = player.transform.position;
        float cx = float.NaN;
        string how = "no room centre";
        RoomSpawner rsp = cot.ThisRoom != null ? cot.ThisRoom.GetComponent<RoomSpawner>() : null;
        if (rsp != null && Mathf.Abs(rsp.centerx) > 0.001f) { cx = rsp.centerx; how = "room centre x " + cx.ToString("0.0"); }
        else
        {
            Vector3 c; string h;
            if (RoomCenter(cot.ThisRoom, out c, out h)) { cx = c.x; how = "room bounds centre x " + cx.ToString("0.0"); }
        }
        Vector3 dir = new Vector3(float.IsNaN(cx) ? 0f : cx - pp.x, 0f, -FPConfig.FaceForwardBias);
        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.back;
        FaceYaw(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, why, "facing into the room (" + how + ", you at x " + pp.x.ToString("0.0") + ")");
    }

    private static void FaceYaw(float target, string why, string note)
    {
        float delta = Mathf.DeltaAngle(head.eulerAngles.y, target);
        root.RotateAround(head.position, Vector3.up, delta);
        rootOffset = root.position - player.transform.position;
        Debug.Log("[FirstPersonLoD] VR facing (" + why + "): yaw " + target.ToString("0") + " " + note + ", turned " + delta.ToString("0") + " deg");
    }

    private static void LogRoom()
    {
        try
        {
            GameObject room = cot.ThisRoom;
            if (room == null) return;
            StringBuilder sb = new StringBuilder("[FirstPersonLoD] VR room " + room.name);
            RoomSpawner rsp = room.GetComponent<RoomSpawner>();
            if (rsp != null) sb.Append("  centerx " + rsp.centerx.ToString("0.0"));
            Vector3 c; string how;
            if (RoomCenter(room, out c, out how)) sb.Append("  middle " + c.ToString("F1") + " from " + how);
            if (player != null)
            {
                float eyeY = player.transform.position.y + eyeOffset;
                Doors[] ds = room.GetComponentsInChildren<Doors>();
                for (int i = 0; i < ds.Length && i < 6; i++)
                {
                    Renderer r = ds[i].GetComponentInChildren<Renderer>();
                    Collider k = ds[i].GetComponent<Collider>();
                    sb.Append("\n  door " + ds[i].name + " at " + ds[i].transform.position.ToString("F2"));
                    if (r != null) sb.Append("  sprite y " + r.bounds.min.y.ToString("0.00") + ".." + r.bounds.max.y.ToString("0.00"));
                    if (k != null) sb.Append("  trigger " + k.bounds.min.ToString("F2") + ".." + k.bounds.max.ToString("F2"));
                    sb.Append("  (eye y " + eyeY.ToString("0.00") + ")");
                }
            }
            Debug.Log(sb.ToString());
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] VR room log failed: " + Flat(e)); }
    }

    // ---- keep the head inside the body so leaning/drift cannot push the eyes through walls ----
    private static void Leash()
    {
        if (FPConfig.HeadLeash <= 0f) return;
        float r = Mathf.Max(bodyRadius * FPConfig.HeadLeash, 0.005f);
        Vector3 d = head.position - player.transform.position;
        d.y = 0f;
        float m = d.magnitude;
        if (m <= r) return;
        Vector3 corr = d * ((m - r) / m);
        root.position -= corr;
        rootOffset -= corr;
    }

    // ---- aim: the game only faces the player left/right (rotation 0 or 180, front = local -X).
    // Turning the player root to head yaw carries the weapon's attack box where you look. Physics
    // runs before the next Update, so hits use this rotation.
    private static void AimAtHead()
    {
        if (!FPConfig.AimWithHead) { if (aimApplied) UnAim(); return; }
        Vector3 f = head.forward; f.y = 0f;
        if (f.sqrMagnitude < 0.0001f) return;
        player.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(f.z, -f.x) * Mathf.Rad2Deg, 0f);
        aimApplied = true;
    }

    private static void UnAim()
    {
        aimApplied = false;
        if (player == null) return;
        // back to the game's nearest left/right facing
        Vector3 f = player.transform.rotation * Vector3.left;
        player.transform.rotation = Quaternion.Euler(0f, f.x > 0f ? 180f : 0f, 0f);
    }

    private static bool Acquire()
    {
        if (detached && root != null && head != null) return true;
        if (cot.VRRig == null) return false;

        // headset camera = enabled stereo camera under the rig
        Camera hc = null;
        Camera[] cams = cot.VRRig.GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < cams.Length; i++)
        {
            if (cams[i].enabled && cams[i].gameObject.activeInHierarchy && cams[i].stereoTargetEye != StereoTargetEyeMask.None)
            { hc = cams[i]; break; }
        }
        if (hc == null)
            for (int i = 0; i < cams.Length; i++)
                if (cams[i].enabled && cams[i].gameObject.activeInHierarchy) { hc = cams[i]; break; }
        head = hc != null ? hc.transform : (cot.VRHead != null ? cot.VRHead.transform : null);
        if (head == null) return false;
        headCam = hc;
        if (headCam != null)
        {
            origFar = headCam.farClipPlane;
            origNear = headCam.nearClipPlane;
            if (FPConfig.FarClip > 0f) headCam.farClipPlane = FPConfig.FarClip;
            Debug.Log("[FirstPersonLoD] VR camera near " + origNear.ToString("0.###") + "  far clip " + origFar.ToString("0.#") + " -> " + headCam.farClipPlane.ToString("0.#")
                + "  pixelLightCount " + QualitySettings.pixelLightCount + "  shadows " + QualitySettings.shadows
                + " (distance " + QualitySettings.shadowDistance.ToString("0.#") + ")"
                + "  AA " + QualitySettings.antiAliasing + "  renderScale " + UnityEngine.VR.VRSettings.renderScale.ToString("0.##")
                + "  stereo " + headCam.stereoTargetEye + "  rendering " + headCam.actualRenderingPath
                + (cot.ThisRoom != null ? "  room " + cot.ThisRoom.name + " at " + cot.ThisRoom.transform.position.ToString("F1") : ""));
            StringBuilder cs = new StringBuilder("[FirstPersonLoD] VR cameras rendering:");
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
                cs.Append("\n  " + all[i].name + "  depth " + all[i].depth + "  eye " + all[i].stereoTargetEye + "  clear " + all[i].clearFlags
                    + "  mask 0x" + all[i].cullingMask.ToString("X") + "  hdr " + all[i].allowHDR);
            Component[] comps = headCam.GetComponents<Component>();
            cs.Append("\n  headset camera components:");
            for (int i = 0; i < comps.Length; i++) if (comps[i] != null) cs.Append(" " + comps[i].GetType().Name);
            Debug.Log(cs.ToString());
        }

        // rig root: topmost of VRScaleObj / VRRig that contains the headset
        Transform a = cot.VRScaleObj != null ? cot.VRScaleObj.transform : null;
        Transform b = cot.VRRig.transform;
        bool aHas = a != null && head.IsChildOf(a);
        bool bHas = head.IsChildOf(b);
        if (aHas && bHas) root = b.IsChildOf(a) ? a : b;
        else root = aHas ? a : b;

        Debug.Log("[FirstPersonLoD] VR rig at acquire:\n" + Hierarchy(head) + "\n  root = " + root.name
            + ", headset camera = " + (hc != null ? hc.name : "(none, using VRHead)"));

        origParent = root.parent;
        origLP = root.localPosition;
        origLR = root.localRotation;
        origLS = root.localScale;
        root.SetParent(null, true);
        detached = true;
        needRecenter = true;
        return true;
    }

    private static MethodInfo setCenter;
    private static bool setCenterTried;

    // ---- door diagnostics: what the game's door thinks while you stand in its box ----
    private static GameObject watchRoom;
    private static Doors[] roomDoors = new Doors[0];
    private static Stairs[] roomStairs = new Stairs[0];
    private static int usedCleared;
    private static Doors inDoor;
    private static float inDoorSince;
    private static bool inDoorLogged;
    private static FieldInfo fDoorLit, fDoorGuys, fDoorWait, fDoorTimer;

    private static FieldInfo DoorField(string plain, string obf)
    {
        BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        return typeof(Doors).GetField(plain, bf) ?? typeof(Doors).GetField(obf, bf);
    }

    private static string DoorState(Doors d)
    {
        if (fDoorLit == null)
        {
            fDoorLit = DoorField("lit", "AAAAAAAAAAAAAAAAAAAAb");
            fDoorGuys = DoorField("Guys", "AAAAAAAAAAAAAAAAAAAfy");
            fDoorWait = DoorField("waitforit", "AAAAAAAAAAAAAAAAgi");
            fDoorTimer = DoorField("timer", "AAAAAAAAAAAAAAAAAAfm");
        }
        StringBuilder sb = new StringBuilder();
        try
        {
            object guys = fDoorGuys != null ? fDoorGuys.GetValue(d) : null;
            int gc = guys is System.Collections.ICollection ? ((System.Collections.ICollection)guys).Count : -1;
            GameObject[] pl = cot.Players;
            sb.Append("lit " + (fDoorLit != null ? fDoorLit.GetValue(d) : "?") + ", JustWent " + d.JustWent + ", waitforit " + (fDoorWait != null ? fDoorWait.GetValue(d) : "?")
                + ", timer " + (fDoorTimer != null ? ((float)fDoorTimer.GetValue(d)).ToString("0.00") : "?") + ", ILastDoor " + d.ILastDoor
                + ", secret " + d.secret + ", in box " + gc + " of " + (pl != null ? pl.Length : 0) + " players needed");
            if (pl != null) for (int i = 0; i < pl.Length; i++)
                sb.Append(i == 0 ? " (" : ", ").Append(pl[i] == null ? "null" : pl[i].name + (pl[i] == player ? " = you" : " at " + pl[i].transform.position.ToString("F1"))).Append(i == pl.Length - 1 ? ")" : "");
            sb.Append(", leads to " + (d.OtherDoor != null ? d.OtherDoor.name : "nothing"));
            if (d.backlight != null) sb.Append(", backlight " + (d.backlight.GetComponent<Light>() != null && d.backlight.GetComponent<Light>().enabled ? "on" : "off"));
        }
        catch (Exception e) { sb.Append("state unreadable: " + e.GetType().Name); }
        return sb.ToString();
    }

    // Previously used passageways. The game marks the door or stairs you arrive through as "just
    // went" and only clears that after it has seen you inside its box and then walking out (plus
    // 1 s). The mod steps you out of the arrival box in the same frame you arrive, so the game never
    // saw you inside, never saw you leave, and the mark stayed forever: that passage would then
    // refuse to send you anywhere, however long you walked into it. Now any such mark is cleared
    // as soon as you are outside that box, so every door and stairs works the same, used or not.
    private static void ClearUsedMarks(Vector3 pp)
    {
        if (!FPConfig.ClearUsedDoors) return;
        for (int i = 0; i < roomDoors.Length; i++)
        {
            Doors d = roomDoors[i];
            if (d == null || !d.JustWent) continue;
            BoxCollider bc = d.GetComponent<BoxCollider>();
            if (bc != null && bc.bounds.Contains(pp)) continue;
            d.JustWent = false;
            if (fDoorWait == null) DoorState(d); // resolves the private fields
            try { if (fDoorWait != null) fDoorWait.SetValue(d, false); if (fDoorTimer != null) fDoorTimer.SetValue(d, 0f); } catch (Exception) { }
            if (usedCleared++ < 40) Debug.Log("[FirstPersonLoD] VR door: " + d.name + " was still marked as just used while you are outside it; cleared, so it will take you through");
        }
        for (int i = 0; i < roomStairs.Length; i++)
        {
            Stairs st = roomStairs[i];
            if (st == null || !st.JustWent) continue;
            BoxCollider bc = st.GetComponent<BoxCollider>();
            if (bc != null && bc.bounds.Contains(pp)) continue;
            st.JustWent = false;
            if (usedCleared++ < 40) Debug.Log("[FirstPersonLoD] VR stairs: " + st.name + " was still marked as just used while you are outside it; cleared, so it will take you through");
        }
    }

    private static void DoorWatch(Vector3 pp)
    {
        if (cot.ThisRoom != watchRoom)
        {
            watchRoom = cot.ThisRoom;
            roomDoors = watchRoom != null ? watchRoom.GetComponentsInChildren<Doors>() : new Doors[0];
            roomStairs = watchRoom != null ? watchRoom.GetComponentsInChildren<Stairs>() : new Stairs[0];
            inDoor = null;
        }
        ClearUsedMarks(pp);
        Doors now = null;
        for (int i = 0; i < roomDoors.Length; i++)
        {
            Doors d = roomDoors[i];
            if (d == null) continue;
            BoxCollider bc = d.GetComponent<BoxCollider>();
            if (bc != null && bc.bounds.Contains(pp)) { now = d; break; }
        }
        float t = Time.realtimeSinceStartup;
        if (now != inDoor)
        {
            if (inDoor != null && inDoorLogged)
                Debug.Log("[FirstPersonLoD] VR door: left " + inDoor.name + "'s box after " + (t - inDoorSince).ToString("0.0") + " s without going through");
            inDoor = now; inDoorSince = t; inDoorLogged = false;
            if (now != null && FPConfig.DoorLog)
                Debug.Log("[FirstPersonLoD] VR door: entered " + now.name + "'s box: " + DoorState(now));
        }
        else if (inDoor != null && !inDoorLogged && t - inDoorSince > 1.5f)
        {
            inDoorLogged = true;
            Debug.Log("[FirstPersonLoD] VR door: standing in " + inDoor.name + "'s box for 1.5 s and the game has not sent you through: " + DoorState(inDoor)
                + (arrivedVia == inDoor.gameObject ? " (this is the door you arrived through)" : ""));
        }
    }

    private static void TryMenuRecenter()
    {
        float now = Time.realtimeSinceStartup;
        Camera c = cot.VRRig != null ? cot.VRRig.GetComponentInChildren<Camera>() : null;
        Vector3 hp; Quaternion hr;
        bool tracked = c != null && c.transform.localPosition.sqrMagnitude > 0.0001f && VRHand.Hmd(out hp, out hr) && hp.sqrMagnitude > 0.0001f;
        if (!tracked) trackedSince = -1f;
        else if (trackedSince < 0f) trackedSince = now;
        if (tracked && now - trackedSince >= 0.3f)
        {
            string why = pendingMenuRecenter;
            pendingMenuRecenter = "";
            string waited = now - pendingSince > 0.5f ? ", waited " + (now - pendingSince).ToString("0.0") + " s for head tracking" : "";
            if (why != "tabletop view" || FPConfig.MenuRecenter) MenuRecenter(why + waited);
            if (why == "tabletop view" && FPConfig.DioramaBelowEye > 0f) PlaceDiorama(c);
        }
        else if (now - pendingSince > 15f)
        {
            Debug.Log("[FirstPersonLoD] VR menu recenter (" + pendingMenuRecenter + ") skipped: the headset reported no tracked position for 15 s");
            pendingMenuRecenter = "";
        }
    }

    // tabletop: move the game's rig (not the world) so the current room's middle sits
    // DioramaBelowEye meters below your eyes
    private static void PlaceDiorama(Camera c)
    {
        try
        {
            Vector3 mid; string how;
            if (c == null || cot.VRRig == null || !RoomCenter(cot.ThisRoom, out mid, out how)) { Debug.Log("[FirstPersonLoD] VR tabletop height: no room or camera, left as is"); return; }
            Transform rig = cot.VRRig.transform;
            Transform sp = rig.parent;
            float unitsPerMeter = sp != null ? Mathf.Abs(sp.lossyScale.y) : 1f;
            if (unitsPerMeter < 0.0001f) unitsPerMeter = 1f;
            float eyeY = c.transform.position.y;
            float before = (eyeY - mid.y) / unitsPerMeter;
            float delta = mid.y + FPConfig.DioramaBelowEye * unitsPerMeter - eyeY; // world units to raise your eyes by
            rig.position += Vector3.up * delta;
            Debug.Log("[FirstPersonLoD] VR tabletop height: room " + (cot.ThisRoom != null ? cot.ThisRoom.name : "?") + " middle was "
                + (before >= 0f ? before.ToString("0.00") + " m below" : (-before).ToString("0.00") + " m above") + " your eyes; now "
                + FPConfig.DioramaBelowEye.ToString("0.00") + " m below (" + unitsPerMeter.ToString("0.#") + " units/m, room " + how + ", rig local now "
                + rig.localPosition.ToString("F3") + ")");
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] VR tabletop height failed: " + Flat(e)); }
    }

    private static void MenuRecenter(string why)
    {
        try
        {
            if (cot.VRRig == null || cot.Cross == null) { Debug.Log("[FirstPersonLoD] VR menu recenter skipped (" + why + "): rig or Cross missing"); return; }
            if (!setCenterTried)
            {
                setCenterTried = true;
                BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                setCenter = typeof(CamOTron).GetMethod("SetVRCenter", bf, null, Type.EmptyTypes, null)
                    ?? typeof(CamOTron).GetMethod("AAAAAAAAAAAAAAAgk", bf, null, Type.EmptyTypes, null);
            }
            if (setCenter == null) { Debug.Log("[FirstPersonLoD] VR menu recenter: CamOTron.SetVRCenter not found"); return; }
            Transform rig = cot.VRRig.transform;
            Camera c = rig.GetComponentInChildren<Camera>();
            Vector3 lp0 = rig.localPosition; float y0 = rig.eulerAngles.y;
            setCenter.Invoke(cot, null);
            Debug.Log("[FirstPersonLoD] VR menu recenter (" + why + "): game view re-centred on your head (not saved to the game's settings). rig local "
                + lp0.ToString("F3") + " yaw " + y0.ToString("0") + " -> " + rig.localPosition.ToString("F3") + " yaw " + rig.eulerAngles.y.ToString("0")
                + (c != null ? "; head " + c.transform.localPosition.ToString("F2") + " m in the tracking space, now at " + c.transform.position.ToString("F1") : "")
                + "; Cross '" + cot.Cross.name + "' parent " + (cot.Cross.parent != null ? cot.Cross.parent.name : "none")
                + (c != null ? ", " + (cot.Cross.position - c.transform.position).magnitude.ToString("0.00") + " units from the head camera" : ""));
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] VR menu recenter failed: " + Flat(e)); }
    }

    private static string Hierarchy(Transform t)
    {
        StringBuilder sb = new StringBuilder();
        for (Transform x = t; x != null; x = x.parent)
            sb.Append("  " + x.name + "  localPos " + x.localPosition.ToString("F3") + "  localScale " + x.localScale.ToString("F3")
                + "  worldPos " + x.position.ToString("F3") + "\n");
        return sb.ToString();
    }

    private static void Release(string why)
    {
        Driving = false;
        if (why != "toggled off (F6)") InvMenu.Close("inventory closed: " + why);
        if (!detached) return;
        if (head != null && cot != null) { savedYaw = head.eulerAngles.y; savedRoom = cot.ThisRoom; hasSavedYaw = true; }
        if (compositorTouched || mirrorAlpha > 0f) { CompositorFade(0f, 0f); compositorTouched = false; }
        mirrorAlpha = 0f;
        fadeKind = "";
        UnhideDrop();
        if (aimApplied) UnAim();
        RoomShape.Show(false);
        BrickBatch.Show(false);
        WeaponView.Release();
        Sword6.Release();
        VRHands.Release();
        OffHand.Release();
        VRHud.Restore();
        Sprites.RestoreFacing();
        VRPerf.Restore();
        if (headCam != null) { headCam.farClipPlane = origFar; headCam.nearClipPlane = origNear; headCam = null; }
        if (root != null)
        {
            root.SetParent(origParent, false);
            root.localPosition = origLP;
            root.localRotation = origLR;
            root.localScale = origLS;
        }
        FPDriver.RestoreBody();
        detached = false;
        root = null;
        head = null;
        Camera gameCam = cot != null && cot.VRRig != null ? cot.VRRig.GetComponentInChildren<Camera>() : null;
        Debug.Log("[FirstPersonLoD] VR rig handed back to the game (" + why + "); render state now: " + VRPerf.State(gameCam));
    }

    private static void Recenter()
    {
        needRecenter = false;
        Transform space = head.parent; // tracking space: head.localPosition is the tracked pose in meters
        float measured = head.localPosition.y;
        physEye = Mathf.Max(measured, FPConfig.MinEyeMeters);

        // eye point from the character's collision body (feet + EyeFrac of its height)
        MeasureBody();

        // scale: game units per meter so that your eye height == character eye height
        float eyeAboveFeet = eyeOffset;
        PlayerMovement2 pm = player.GetComponent<PlayerMovement2>();
        CharacterController cc = pm != null && pm.Con != null ? pm.Con : player.GetComponent<CharacterController>();
        if (cc != null && FPConfig.EyeFrac > 0f)
            eyeAboveFeet = Mathf.Max(cc.height * Mathf.Abs(cc.transform.lossyScale.y), 2f * bodyRadius) * FPConfig.EyeFrac;
        worldScale = (Mathf.Max(eyeAboveFeet, 0.01f) / physEye) * FPConfig.VRScaleMul;
        float spaceScale = space != null ? space.lossyScale.y : 1f;
        if (spaceScale > 0.00001f) root.localScale = root.localScale * (worldScale / spaceScale);

        // near clip in world units: the game's value was sized for the tabletop view (7 units/m) and
        // clipped everything within about a meter of your eyes at first-person scale
        if (headCam != null && FPConfig.NearMeters > 0f)
            headCam.nearClipPlane = Mathf.Max(FPConfig.NearMeters * worldScale, 0.001f);

        // place: move the rig so the headset lands on the character's eyes
        Vector3 eye = player.transform.position + Vector3.up * eyeOffset;
        root.position += eye - head.position;
        rootOffset = root.position - player.transform.position;

        Say("VR recentered. measured eye " + measured.ToString("0.00") + " m"
            + (measured < FPConfig.MinEyeMeters ? " (seated origin? using " + FPConfig.MinEyeMeters.ToString("0.00") + ")" : "")
            + ", world scale " + worldScale.ToString("0.000") + " units/m, near clip " + (headCam != null ? headCam.nearClipPlane.ToString("0.0000") : "?")
            + "\n  body: " + bodyNote);
    }

    private static void SnapTurn(float deg)
    {
        root.position = player.transform.position + rootOffset; // pivot where the head is this frame
        root.RotateAround(head.position, Vector3.up, deg);
        rootOffset = root.position - player.transform.position;
    }

    // controller: hold next item (right B) + previous item (left trigger) for 2 s (first person and tabletop)
    private static void GiveAllCombo()
    {
        bool gn, gp;
        if (FPConfig.GiveAllCombo && !InvMenu.Blocking && ReadHeld("Next", ref hNextH, out gn) && ReadHeld("Prev", ref hPrevH, out gp) && gn && gp)
        {
            if (giveHeld == 0f) Debug.Log("[FirstPersonLoD] VR: B + left trigger held (give all items after 2 s)");
            giveHeld += Time.unscaledDeltaTime;
            if (giveHeld >= 2f) { giveHeld = -999f; Say("Debug: " + GiveAll.Run(player)); }
        }
        else giveHeld = 0f;
    }

    private static Camera tabletopCam;
    private static Transform TabletopHead()
    {
        if (tabletopCam == null || !tabletopCam.isActiveAndEnabled)
            tabletopCam = cot != null && cot.VRRig != null ? cot.VRRig.GetComponentInChildren<Camera>() : null;
        return tabletopCam != null && tabletopCam.transform.parent != null ? tabletopCam.transform : null;
    }

    private static void HandleKeys()
    {
        if (Input.GetKeyDown(FPConfig.RecenterKey)) needRecenter = true;
        if (Input.GetKeyDown(KeyCode.LeftBracket)) SnapTurn(-FPConfig.SnapTurn);
        if (Input.GetKeyDown(KeyCode.RightBracket)) SnapTurn(FPConfig.SnapTurn);
        if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.Equals))
        {
            float step = Input.GetKeyDown(KeyCode.Minus) ? -0.05f : 0.05f;
            if (FPConfig.EyeFrac > 0f) { FPConfig.EyeFrac = Mathf.Clamp(FPConfig.EyeFrac + step, 0.3f, 1.2f); Say("Eye at " + (FPConfig.EyeFrac * 100f).ToString("0") + "% of body height"); }
            else FPConfig.EyeHeight = Mathf.Max(0.05f, FPConfig.EyeHeight + step);
            needRecenter = true;
        }
        if (Input.GetKeyDown(KeyCode.Home)) { needFace = true; faceWhy = "Home key"; }
        if (Input.GetKeyDown(KeyCode.F4)) { FPConfig.AimWithHead = !FPConfig.AimWithHead; Say("Aim with head: " + FPConfig.AimWithHead); }
        if (Input.GetKeyDown(KeyCode.Insert)) { FPConfig.WeaponMirror = !FPConfig.WeaponMirror; Say("Weapon mirrored: " + FPConfig.WeaponMirror); }
        if (Input.GetKeyDown(KeyCode.Semicolon) || Input.GetKeyDown(KeyCode.Quote))
        {
            float st = Input.GetKeyDown(KeyCode.Semicolon) ? -10f : 10f;
            if (Sword6.isGun) { FPConfig.GunPitch += st; Say("Gun angle " + FPConfig.GunPitch.ToString("0") + " (F7 saves)"); }
            else { FPConfig.WeaponPitch += st; Say("Weapon angle " + FPConfig.WeaponPitch.ToString("0") + " (F7 saves)"); }
        }
        if (Input.GetKeyDown(KeyCode.Slash)) { FPConfig.WeaponGripFlip = !FPConfig.WeaponGripFlip; Sword6.Rebuild(); Say("Sword grip end flipped: " + FPConfig.WeaponGripFlip); }
        if (Input.GetKeyDown(KeyCode.F3)) { FPConfig.FPRenderPath = FPConfig.FPRenderPath == "forward" ? "game" : "forward"; VRPerf.Restore(); Say("Render path: " + FPConfig.FPRenderPath); }
        if (Input.GetKeyDown(KeyCode.Comma)) { FPConfig.VRScaleMul = Mathf.Max(0.1f, FPConfig.VRScaleMul - 0.1f); needRecenter = true; }
        if (Input.GetKeyDown(KeyCode.Period)) { FPConfig.VRScaleMul += 0.1f; needRecenter = true; }
        if (Input.GetKeyDown(KeyCode.F7)) { FPConfig.Save(); Say("Saved " + FPConfig.CfgPath()); }
        if (Input.GetKeyDown(KeyCode.F2) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) Say("Debug: " + GiveAll.Run(player));
        GiveAllCombo();
        if (FPConfig.GiveAllItems && player != givenTo) { givenTo = player; Say("Debug (GiveAllItems=true): " + GiveAll.Run(player)); }
        if (Input.GetKeyDown(KeyCode.F8)) { FPConfig.BillboardMode = (FPConfig.BillboardMode + 1) % 3; FPDriver.ResetSpriteCache(); Say("Billboard mode " + FPConfig.BillboardMode); }
        if (Input.GetKeyDown(KeyCode.F9)) { FPConfig.HideOwnBody = !FPConfig.HideOwnBody; Say("Hide own sprite: " + FPConfig.HideOwnBody); }
        if (Input.GetKeyDown(KeyCode.F10)) { FPConfig.FlipBillboard = !FPConfig.FlipBillboard; Say("Billboard flip: " + FPConfig.FlipBillboard); }
        string tk = Sprites.HandleKeys();
        if (tk != null) Say(tk);

        // controller snap turn: TurnLeft / TurnRight actions (added to the game's manifest by
        // install-vr-bindings; right stick left/right). Read straight from OpenVR, rising edge only.
        bool tl, tr;
        if (!InvMenu.Blocking && ReadTurn(out tl, out tr))
        {
            if (tl && !turnLeftWas) SnapTurn(-FPConfig.SnapTurn);
            if (tr && !turnRightWas) SnapTurn(FPConfig.SnapTurn);
            turnLeftWas = tl;
            turnRightWas = tr;
        }

        // controller recenter: the Recenter action (Steam Frame: left View button) held 1.5 s
        bool rc;
        if (ReadHeld("Recenter", ref hRecenterH, out rc) && rc)
        {
            if (recenterHeld == 0f) Debug.Log("[FirstPersonLoD] VR: recenter button held (recenters after 1.5 s)");
            recenterHeld += Time.unscaledDeltaTime;
            if (recenterHeld >= 1.5f) { recenterHeld = -999f; needRecenter = true; }
        }
        else recenterHeld = 0f;
        // controller recenter: both hotkeys held
        VRInputs vi = VRInputs.inst;
        if (vi != null && vi.HotA && vi.HotB)
        {
            comboHeld += Time.unscaledDeltaTime;
            if (comboHeld >= 1.5f) { comboHeld = -999f; needRecenter = true; }
        }
        else comboHeld = 0f;
    }

    public static string Describe()
    {
        string s = "VR: " + (cot == null ? "no CamOTron" : cot.VR ? "on" : "off (flat launch)")
            + "  FP:" + (enabledFP ? "enabled" : "disabled") + "  driving:" + Driving
            + "  Harmony: " + harmonyNote;
        s += "\n" + lastInputDiag;
        if (Driving)
            s += "\n  eye " + physEye.ToString("0.00") + " m  scale " + worldScale.ToString("0.000")
                + " u/m  root " + (root != null ? root.name : "?")
                + "  move " + (VRInputs.inst != null ? VRInputs.inst.Move.ToString("F2") : "?")
                + "\n  body: " + bodyNote
                + "\n  render: " + VRPerf.Note + "  near " + (headCam != null ? headCam.nearClipPlane.ToString("0.0000") : "?")
                + "\n  lights reaching you: " + VRPerf.NearNote
                + "\n  transition darkness: headset " + mirrorAlpha.ToString("0.00") + (fadeKind.Length > 0 ? " (" + fadeKind + " fade)" : "")
                + "\n  weapon: " + (Sword6.Active ? "in hand: " + Sword6.Note : "flat view: " + WeaponView.Note) + "  aim with head: " + FPConfig.AimWithHead;
        return s;
    }
}

// ---------------------------------------------------------------------------------------------
// Sprite sheet dump (diagnostics): the first time first person hides a renderer of your character,
// its texture is saved as a PNG under BepInEx/FirstPersonLoD_dump, so the art (for example the
// Knight's shield, which is painted into the body sprite, not a separate object) can be inspected.
// Read through a RenderTexture, so it works on textures the game did not mark readable.
// ---------------------------------------------------------------------------------------------
public static class SpriteDump
{
    private static readonly Dictionary<string, bool> done = new Dictionary<string, bool>();

    public static void Save(Renderer r, string owner)
    {
        if (!FPConfig.DumpSprites || r == null) return;
        try
        {
            Material m = r.sharedMaterial;
            Texture tex = m != null ? m.mainTexture : null;
            if (tex == null) return;
            string key = owner + "_" + tex.name;
            if (done.ContainsKey(key)) return;
            done[key] = true;
            string dir = Path.Combine(Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "BepInEx"), "FirstPersonLoD_dump");
            Directory.CreateDirectory(dir);
            string safe = key;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            string file = Path.Combine(dir, safe + ".png");
            RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            Texture2D copy = new Texture2D(tex.width, tex.height, TextureFormat.ARGB32, false);
            copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(file, copy.EncodeToPNG());
            UnityEngine.Object.Destroy(copy);
            Vector2 sc = m.HasProperty("_MainTex") ? m.GetTextureScale("_MainTex") : Vector2.one;
            Debug.Log("[FirstPersonLoD] VR sprite sheet saved: " + file + " (" + tex.width + "x" + tex.height + ", frame scale " + sc.ToString("F3")
                + ", renderer " + r.name + ")");
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] VR sprite sheet dump failed for " + r.name + ": " + e.GetType().Name + ": " + e.Message); }
    }
}

// ---------------------------------------------------------------------------------------------
// Room shape for first person: deeper rooms with a real front wall.
// Every generated room is a row of tiles (plus end caps) laid along the room's length, all built
// the same way (read from the game's own level data): a floor of half-unit bricks, a back wall of
// bricks, and collision that is NOT the bricks but a few invisible boxes per tile:
//   1Frontblock  thin box along the open front edge (on an end cap it is the end side wall)
//   1Topblock    floor and ceiling slabs (on NPC rooms also short side walls at the back)
//   ForceField   big trigger zones around the tile that push escaped things back inside
// Deepening a room by RoomExtraDepth (tile units; one brick row = 1):
//   - every brick touching the front edge is copied forward until the new strip is covered
//     (floor rows, and on end caps the end walls), in its own material
//   - a wall of bricks is stood along the new front edge, as tall as the tile
//   - the front invisible box and the front ForceField move forward by the same amount; floor,
//     ceiling and end-cap side boxes that touch the front are lengthened to match
// New bricks are plain geometry (mesh, renderer, box collider), never full copies: the game's
// bricks can carry scripts (music tags, sign text) that must not be duplicated. A new brick that
// would overlap anything that is not a brick (a torch, a switch, a door) is left out.
// Torches (lights under a Fireswitch) are the game's only room lighting. The strip added in front
// is farther from the back-wall torches than the old front edge was, so with RoomLightReach each
// torch's range grows by (distance to the new front) / (distance to the old front): the new front
// gets the light the old front had, and the torch itself, its wall and its flicker are unchanged.
// Only while first person drives: the tabletop view (and pause/menus, which show it) get the
// game's original rooms back, colliders and torch ranges included, and first person puts the
// changes back on. Enemies and spawn points are unchanged, so they still start in the original
// strip.
// ---------------------------------------------------------------------------------------------
public static class RoomShape
{
    private class Mod
    {
        public GameObject room;
        public readonly List<GameObject> added = new List<GameObject>();
        public readonly List<Transform> moved = new List<Transform>();
        public readonly List<Vector3> origPos = new List<Vector3>(), origScale = new List<Vector3>();
        public readonly List<Vector3> newPos = new List<Vector3>(), newScale = new List<Vector3>();
        public readonly List<Light> lights = new List<Light>();
        public readonly List<float> origRange = new List<float>(), newRange = new List<float>();
    }
    private static readonly Dictionary<int, Mod> mods = new Dictionary<int, Mod>();
    private static bool shown = true;
    private static int logged;

    private static float ZMin(Transform t) { return t.localPosition.z - Mathf.Abs(t.localScale.z) * 0.5f; }
    private static float ZMax(Transform t) { return t.localPosition.z + Mathf.Abs(t.localScale.z) * 0.5f; }

    private static void Change(Mod m, Transform t, Vector3 pos, Vector3 scale)
    {
        m.moved.Add(t); m.origPos.Add(t.localPosition); m.origScale.Add(t.localScale);
        m.newPos.Add(pos); m.newScale.Add(scale);
        t.localPosition = pos; t.localScale = scale;
    }

    // brick extent along the tile's z (local), from its renderer bounds
    private static bool LocalZ(Transform tile, Renderer r, out float zmin, out float zmax)
    {
        Bounds b = r.bounds;
        Vector3 a = tile.InverseTransformPoint(b.min), c = tile.InverseTransformPoint(b.max);
        zmin = Mathf.Min(a.z, c.z); zmax = Mathf.Max(a.z, c.z);
        return zmax - zmin > 0.01f;
    }

    // everything in a tile a new brick must not cut into: visible non-brick objects and lights
    private static List<Bounds> Obstacles(Transform tile)
    {
        List<Bounds> obs = new List<Bounds>();
        Renderer[] rs = tile.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null) continue;
            Transform t = r.transform;
            if (t.parent == tile && t.name == "SmallBlock") continue;
            if (t.name == "FPBrick") continue;   // first-person brick copies
            if (!(r is MeshRenderer) || r.GetComponent<TextMesh>() != null) continue;
            // small things only (torch boxes, levers, doors): water, lava and other big planes are
            // part of the floor and must not stop the floor from being extended
            Vector3 sz = r.bounds.size;
            if (sz.sqrMagnitude < 1e-6f || Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z)) > 2.5f) continue;
            obs.Add(r.bounds);
        }
        Light[] ls = tile.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < ls.Length; i++) if (ls[i] != null) obs.Add(new Bounds(ls[i].transform.position, Vector3.one * 0.3f));
        return obs;
    }

    private static bool Overlaps(Bounds b, List<Bounds> obs)
    {
        b.Expand(-0.1f);
        for (int i = 0; i < obs.Count; i++) if (obs[i].Intersects(b)) return true;
        return false;
    }

    // a plain brick: the source's mesh, material, layer and box collider, nothing else
    private static GameObject PlainBrick(Transform src, Transform parent, Material mat, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        MeshFilter smf = src.GetComponent<MeshFilter>();
        MeshRenderer smr = src.GetComponent<MeshRenderer>();
        if (smf == null || smr == null || smf.sharedMesh == null) return null;
        GameObject g = new GameObject("SmallBlock");
        g.layer = src.gameObject.layer;
        try { if (src.tag != "MusicObj" && src.tag != "Untagged") g.tag = src.tag; } catch (Exception) { }
        Transform t = g.transform;
        t.SetParent(parent, false);
        t.localPosition = pos; t.localRotation = rot; t.localScale = scale;
        g.AddComponent<MeshFilter>().sharedMesh = smf.sharedMesh;
        MeshRenderer mr = g.AddComponent<MeshRenderer>();
        mr.sharedMaterials = mat != null ? new Material[] { mat } : smr.sharedMaterials;
        mr.shadowCastingMode = smr.shadowCastingMode;
        mr.receiveShadows = smr.receiveShadows;
        mr.lightProbeUsage = smr.lightProbeUsage;
        mr.reflectionProbeUsage = smr.reflectionProbeUsage;
        BoxCollider sb = src.GetComponent<BoxCollider>();
        if (sb != null)
        {
            BoxCollider b = g.AddComponent<BoxCollider>();
            b.center = sb.center; b.size = sb.size; b.isTrigger = sb.isTrigger; b.sharedMaterial = sb.sharedMaterial;
        }
        return g;
    }

    private static bool Keep(Mod m, GameObject g, List<Bounds> obs, List<Bounds> addedBounds, ref int skipped)
    {
        if (g == null) return false;
        Bounds b = g.GetComponent<MeshRenderer>().bounds;
        if (Overlaps(b, obs)) { UnityEngine.Object.DestroyImmediate(g); skipped++; return false; }
        m.added.Add(g); addedBounds.Add(b);
        return true;
    }

    public static void Room(GameObject room)
    {
        if (!FPConfig.RoomReshape || room == null || FPConfig.RoomExtraDepth <= 0f) return;
        int id = room.GetInstanceID();
        if (mods.ContainsKey(id)) return;
        // a room not switched on yet (the first room of a run) has no sizes to go by: its
        // renderers and colliders read as empty. BrickBatch retries it once it is on.
        if (!room.activeInHierarchy) return;
        Mod m = new Mod(); m.room = room;
        mods[id] = m;
        try
        {
            float D = FPConfig.RoomExtraDepth;
            Transform r = room.transform;
            // the front edge (tile-local z of the inner face of the front walls), shared by the room
            List<float> edges = new List<float>();
            for (int i = 0; i < r.childCount; i++)
            {
                Transform tile = r.GetChild(i);
                for (int j = 0; j < tile.childCount; j++)
                {
                    Transform c = tile.GetChild(j);
                    if (c.name == "1Frontblock" && Mathf.Abs(c.localScale.x) >= Mathf.Abs(c.localScale.z) && c.localPosition.z < 1f) edges.Add(ZMax(c));
                }
            }
            if (edges.Count == 0)
            {
                if (room.name == "Tavern" && FPConfig.TavernWalls) { TavernWalls(room, m); return; }
                if (logged++ < 20) Debug.Log("[FirstPersonLoD] VR room shape: " + room.name + " is not a tiled room, left as is");
                return;
            }
            edges.Sort();
            float edge = edges[edges.Count / 2];

            // One wall line for the whole room. Tiles are not all placed at the same depth (some sit
            // set back from their neighbours), so each tile's own front edge is found in room space and
            // the tile is deepened by however much it takes to reach the shared line: the room's most
            // forward front minus RoomExtraDepth. Before, every tile got the same depth from its own
            // front, which left a set-back tile's wall set back too, with open gaps at its sides.
            Dictionary<Transform, float> tileEdge = new Dictionary<Transform, float>();
            Dictionary<Transform, float> tileFrontRoom = new Dictionary<Transform, float>();
            float wallRoom = float.MaxValue;
            for (int i = 0; i < r.childCount; i++)
            {
                Transform tile = r.GetChild(i);
                float best = float.MaxValue;
                for (int j = 0; j < tile.childCount; j++)
                {
                    Transform c = tile.GetChild(j);
                    if (c.name == "1Frontblock" && Mathf.Abs(c.localScale.x) >= Mathf.Abs(c.localScale.z) && c.localPosition.z < 1f && Mathf.Abs(ZMax(c) - edge) < 1.6f)
                        best = Mathf.Min(best, ZMax(c));
                }
                float te = best < float.MaxValue ? best : edge;
                tileEdge[tile] = te;
                float fr = r.InverseTransformPoint(tile.TransformPoint(new Vector3(0f, 0f, te))).z;
                tileFrontRoom[tile] = fr;
                if (best < float.MaxValue) wallRoom = Mathf.Min(wallRoom, fr - D);
            }
            if (wallRoom == float.MaxValue) wallRoom = edge - D;
            StringBuilder setBack = new StringBuilder();

            // wall height for tiles that have no ceiling of their own (end caps, stairwells): the room's usual
            List<float> tops = new List<float>();
            for (int i = 0; i < r.childCount; i++)
            {
                float ceil = Ceiling(r.GetChild(i));
                if (ceil < float.MaxValue) tops.Add(ceil + 0.5f);
            }
            tops.Sort();
            float roomTop = tops.Count > 0 ? tops[tops.Count / 2] : 2.5f;

            int tiles = 0, bricksCopied = 0, wallBricks = 0, boxes = 0, skipped = 0, floorFilled = 0, openings = 0;
            StringBuilder odd = new StringBuilder();
            List<Bounds> addedBounds = new List<Bounds>();
            Transform roomSample = null; Material roomMat = null;
            for (int i = 0; i < r.childCount; i++)
            {
                Transform tile = r.GetChild(i);
                if (tile.GetComponent<Rigidbody>() != null || tile.GetComponent<Animation>() != null) continue;
                // this tile's own front edge, and how far it has to reach to meet the room's wall line
                float tEdge = tileEdge.ContainsKey(tile) ? tileEdge[tile] : edge;
                float tD = D;
                if (tileFrontRoom.ContainsKey(tile)) tD = Mathf.Clamp(tileFrontRoom[tile] - wallRoom, 0.25f, D + 3f);
                bool isTile = false;
                Transform frontWall = null;
                Transform sample = null;
                Dictionary<Material, int> matCount = new Dictionary<Material, int>();
                Dictionary<Material, int> floorMatCount = new Dictionary<Material, int>();
                float bottom = -0.5f;
                float ceil = Ceiling(tile);
                float width = -1f;
                bool protrudes = false;
                List<Transform> kids = new List<Transform>();
                for (int j = 0; j < tile.childCount; j++) kids.Add(tile.GetChild(j));
                foreach (Transform c in kids)
                {
                    if (c.name == "1Frontblock" || c.name == "1Topblock" || c.name == "ForceField") isTile = true;
                    if (c.name == "1End" && c.localPosition.x > 0.1f) width = c.localPosition.x;
                    if (c.name == "1Frontblock" && Mathf.Abs(c.localScale.x) >= Mathf.Abs(c.localScale.z) && Mathf.Abs(ZMax(c) - tEdge) < 0.3f) frontWall = c;
                }
                if (!isTile) continue;
                tiles++;
                if (Mathf.Abs(tD - D) > 0.05f && setBack.Length < 300) setBack.Append(" " + tile.name + " " + (tD - D >= 0 ? "+" : "") + (tD - D).ToString("0.##"));
                // a tile whose own walls reach well past its front edge (a stairwell going down toward you):
                // its space continues through the new wall, so it keeps its colliders and gets a doorway
                if (frontWall == null)
                    foreach (Transform c in kids)
                        if ((c.name == "1Frontblock" || c.name == "1sidelock" || c.name == "1Topblock") && c.GetComponent<BoxCollider>() != null
                            && !c.GetComponent<BoxCollider>().isTrigger && ZMin(c) < tEdge - 0.5f) protrudes = true;
                foreach (Transform c in kids)
                {
                    if (c.name != "SmallBlock") continue;
                    MeshRenderer mr = c.GetComponent<MeshRenderer>();
                    if (mr == null || !c.gameObject.activeSelf) continue;
                    if (sample == null && c.GetComponent<BoxCollider>() != null && c.GetComponent<MeshFilter>() != null) sample = c;
                    Material mt = mr.sharedMaterial;
                    if (mt == null) continue;
                    int n; matCount.TryGetValue(mt, out n); matCount[mt] = n + 1;
                    if (c.localPosition.y < 0f) { floorMatCount.TryGetValue(mt, out n); floorMatCount[mt] = n + 1; }
                }
                if (sample == null) sample = roomSample;
                Material best = Most(matCount) ?? roomMat;
                Material floorMat = Most(floorMatCount) ?? best;
                if (sample != null && roomSample == null) { roomSample = sample; roomMat = best; }
                List<Bounds> obs = Obstacles(tile);

                if (protrudes)
                {
                    if (sample == null) continue;
                    int placed = Doorway(m, tile, kids, sample, best, tEdge, tD, width > 0f ? width : 2f, roomTop, obs, addedBounds, ref skipped, ref openings);
                    wallBricks += placed;
                    if (odd.Length < 300) odd.Append(" " + tile.name + " (doorway, " + placed + " bricks)");
                    continue;
                }

                foreach (Transform c in kids)
                {
                    if (c.name == "1Frontblock")
                    {
                        bool thinZ = Mathf.Abs(c.localScale.x) >= Mathf.Abs(c.localScale.z);
                        if (c == frontWall)
                        {
                            Change(m, c, c.localPosition + new Vector3(0f, 0f, -tD), c.localScale); boxes++;
                        }
                        else if (!thinZ && ZMin(c) <= tEdge + 0.6f)
                        {
                            // end cap side wall: lengthen forward
                            Vector3 sc = c.localScale; sc.z = Mathf.Abs(sc.z) + tD;
                            Change(m, c, c.localPosition + new Vector3(0f, 0f, -tD * 0.5f), sc); boxes++;
                        }
                    }
                    else if (c.name == "1Topblock" && ZMin(c) <= tEdge + 0.6f)
                    {
                        Vector3 sc = c.localScale; sc.z = Mathf.Abs(sc.z) + tD;
                        Change(m, c, c.localPosition + new Vector3(0f, 0f, -tD * 0.5f), sc); boxes++;
                    }
                    else if (c.name == "ForceField")
                    {
                        if (ZMax(c) <= tEdge + 0.3f) { Change(m, c, c.localPosition + new Vector3(0f, 0f, -tD), c.localScale); boxes++; }
                        else if (ZMin(c) <= tEdge + 0.6f)
                        {
                            Vector3 sc = c.localScale; sc.z = Mathf.Abs(sc.z) + tD;
                            Change(m, c, c.localPosition + new Vector3(0f, 0f, -tD * 0.5f), sc); boxes++;
                        }
                    }
                    else if (c.name == "SmallBlock")
                    {
                        MeshRenderer mr = c.GetComponent<MeshRenderer>();
                        if (mr == null || !c.gameObject.activeSelf) continue;
                        float zmin, zmax;
                        if (!LocalZ(tile, mr, out zmin, out zmax) || zmin > tEdge + 0.1f) continue;
                        float ext = zmax - zmin;
                        int copies = Mathf.CeilToInt(tD / ext - 0.01f);
                        for (int k = 1; k <= copies; k++)
                        {
                            GameObject g = PlainBrick(c, tile, null, c.localPosition + new Vector3(0f, 0f, -ext * k), c.localRotation, c.localScale);
                            if (Keep(m, g, obs, addedBounds, ref skipped)) bricksCopied++;
                        }
                    }
                }
                if (sample == null) continue;

                // floor cells of the new strip that no copied brick covers (the front row there was a fire
                // square, a switch plate or other non-brick floor): a plain floor brick in the tile's floor colour
                floorFilled += FillFloor(m, tile, kids, sample, floorMat, tEdge, tD, width, obs, addedBounds, ref skipped);

                // the new front wall: upright bricks (1 tall, half a unit thick) along the tile's whole front
                if (FPConfig.RoomFrontWall)
                {
                    float x0, x1;
                    if (frontWall != null)
                    {
                        x0 = frontWall.localPosition.x - Mathf.Abs(frontWall.localScale.x) * 0.5f;
                        x1 = frontWall.localPosition.x + Mathf.Abs(frontWall.localScale.x) * 0.5f;
                    }
                    else if (width > 0f) { x0 = 0f; x1 = width; }   // end caps: close the corner too
                    else continue;
                    float zw = tEdge - tD - 0.25f;
                    float top = ceil < float.MaxValue ? ceil + 0.5f : frontWall != null ? Mathf.Max(roomTop, frontWall.localPosition.y + Mathf.Abs(frontWall.localScale.y) * 0.5f) : roomTop;
                    for (float x = x0 + 0.25f; x < x1 - 0.1f; x += 0.5f)
                        for (float y = bottom; y < top - 0.05f; y += 1f)
                        {
                            // mesh length (local z) points up
                            GameObject g = PlainBrick(sample, tile, best, new Vector3(x, y, zw), Quaternion.Euler(-90f, 0f, 0f), sample.localScale);
                            if (Keep(m, g, obs, addedBounds, ref skipped)) wallBricks++;
                        }
                }
            }
            string ceiling = Ceilings(m, r, roomSample, addedBounds);
            string torches = Torches(m, r, edge, D, addedBounds);
            if (logged++ < 40)
                Debug.Log("[FirstPersonLoD] VR room shape: " + room.name + " made " + D.ToString("0.##") + " tile units deeper: " + tiles + " tiles, "
                    + bricksCopied + " bricks copied forward, " + floorFilled + " floor gaps filled, " + wallBricks + " front wall bricks"
                    + (skipped > 0 ? " (" + skipped + " left out: they would cut into a torch, switch or door)" : "")
                    + ", " + boxes + " collision/force boxes moved (front edge at z " + edge.ToString("0.00") + ")"
                    + (odd.Length > 0 ? "\n  tiles that continue past the wall:" + odd + ", " + openings + " open cells" : "")
                    + (setBack.Length > 0 ? "\n  tiles set back or forward from the wall line (extra depth to reach it):" + setBack : "")
                    + "\n  " + ceiling
                    + "\n  " + torches);
            if (!shown) Apply(m, false);
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] VR room shape: " + room.name + " failed: " + e.GetType().Name + ": " + e.Message); }
    }

    // Ceilings. A tiled room's roof is an invisible slab (1Topblock, half a unit thick, above the
    // back wall's top row) that the tabletop camera never shows; in first person looking up shows
    // nothing. Each tile's roof slab gets a layer of bricks inside it (FillBox: running bond, the
    // room's own stone materials in their proportions), from the new front wall to the back wall
    // (not behind it), skipping anything visible already there (the upper floor of a two-tier
    // room, bricks the game put there, torches). No colliders. Shown only in first person.
    private static readonly Dictionary<int, bool> ceilingIds = new Dictionary<int, bool>();
    public static bool IsCeiling(Transform t) { return ceilingIds.ContainsKey(t.GetInstanceID()); }

    private static string Ceilings(Mod m, Transform r, Transform sample, List<Bounds> addedBounds)
    {
        if (!FPConfig.RoomCeilings) return "ceiling: off (RoomCeilings=false)";
        if (sample == null) return "ceiling: no brick to copy";
        MeshFilter smf = sample.GetComponent<MeshFilter>();
        MeshRenderer smr = sample.GetComponent<MeshRenderer>();
        if (smf == null || smr == null || smf.sharedMesh == null) return "ceiling: no brick to copy";
        Dictionary<Material, int> mc = new Dictionary<Material, int>();
        List<Bounds> obs = new List<Bounds>();
        Renderer[] rs = r.GetComponentsInChildren<Renderer>(false);
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer x = rs[i];
            if (x == null || !x.enabled || !(x is MeshRenderer) || x.name == "FPBrick") continue;
            if (x.name == "SmallBlock")
            {
                Material mt = x.sharedMaterial;
                if (mt != null && mt.mainTexture != null && mt.mainTexture.name.StartsWith("Block")) { int n; mc.TryGetValue(mt, out n); mc[mt] = n + 1; }
            }
            Vector3 sz = x.bounds.size;
            if (sz.sqrMagnitude < 1e-6f || Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z)) > 3f) continue;   // backdrops and big planes
            obs.Add(x.bounds);
        }
        Light[] ls = r.GetComponentsInChildren<Light>(false);
        for (int i = 0; i < ls.Length; i++) if (ls[i] != null) obs.Add(new Bounds(ls[i].transform.position, Vector3.one * 0.3f));
        obs.AddRange(addedBounds);
        List<Material> mats = new List<Material>(); List<int> weights = new List<int>(); int total = 0;
        foreach (KeyValuePair<Material, int> kv in mc) { mats.Add(kv.Key); weights.Add(kv.Value); total += kv.Value; }
        if (total == 0) return "ceiling: no stone bricks in the room to copy";
        int slabs = 0, made = 0, covered = 0, before = m.added.Count;
        for (int i = 0; i < r.childCount; i++)
        {
            Transform tile = r.GetChild(i);
            if (tile.GetComponent<Rigidbody>() != null || tile.GetComponent<Animation>() != null) continue;
            float width = -1f, backZ = float.MaxValue;
            for (int j = 0; j < tile.childCount; j++)
            {
                Transform c = tile.GetChild(j);
                if (c.name == "1End" && c.localPosition.x > 0.1f) width = c.localPosition.x;
                if (c.name == "1Frontblock" && Mathf.Abs(c.localScale.x) >= Mathf.Abs(c.localScale.z) && c.localPosition.z > 1f) backZ = Mathf.Min(backZ, ZMin(c));
            }
            for (int j = 0; j < tile.childCount; j++)
            {
                Transform c = tile.GetChild(j);
                if (c.name != "1Topblock" || c.localPosition.y <= 1f) continue;
                BoxCollider bc = c.GetComponent<BoxCollider>();
                if (bc == null || bc.isTrigger) continue;
                Vector3 sc = c.localScale;
                Vector3 ctr = c.localPosition + Vector3.Scale(bc.center, sc);
                Vector3 size = Vector3.Scale(bc.size, new Vector3(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
                if (size.y > 1.01f || size.x <= size.y || size.z <= size.y) continue;   // a roof slab, not a wall or a solid block
                Vector3 lo = ctr - size * 0.5f, hi = ctr + size * 0.5f;
                if (backZ < float.MaxValue) hi.z = Mathf.Min(hi.z, backZ + 0.25f);
                if (width > 0f) { lo.x = Mathf.Max(lo.x, -0.25f); hi.x = Mathf.Min(hi.x, width + 0.25f); }
                if (hi.z - lo.z < 0.45f || hi.x - lo.x < 0.45f) continue;
                Bounds wb = new Bounds(tile.TransformPoint(lo), Vector3.zero);
                for (int k = 1; k < 8; k++) wb.Encapsulate(tile.TransformPoint(new Vector3((k & 1) != 0 ? hi.x : lo.x, (k & 2) != 0 ? hi.y : lo.y, (k & 4) != 0 ? hi.z : lo.z)));
                int cov;
                int got = FillBox(wb, r, smf.sharedMesh, smr, mats, weights, total, obs, m.added, out cov);
                if (got < 0) continue;
                slabs++; made += got; covered += cov;
            }
        }
        for (int i = before; i < m.added.Count; i++)
        {
            if (m.added[i] == null) continue;
            ceilingIds[m.added[i].transform.GetInstanceID()] = true;
            // overhead, above the torches: its shadows would fall on nothing you see, but a
            // shadowed point light would draw every ceiling brick six more times
            MeshRenderer cmr = m.added[i].GetComponent<MeshRenderer>();
            if (cmr != null) cmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        return "ceiling: " + made + " bricks under " + slabs + " roof slabs (" + covered + " places already taken by something visible)";
    }

    private static Material Most(Dictionary<Material, int> counts)
    {
        Material best = null; int bn = -1;
        foreach (KeyValuePair<Material, int> kv in counts) if (kv.Value > bn) { bn = kv.Value; best = kv.Key; }
        return best;
    }

    // underside of a tile's upper slab (the slab itself can be a tall solid block); MaxValue = none
    private static float Ceiling(Transform tile)
    {
        float ceil = float.MaxValue;
        for (int j = 0; j < tile.childCount; j++)
        {
            Transform c = tile.GetChild(j);
            if (c.name == "1Topblock" && Mathf.Abs(c.localScale.x) > Mathf.Abs(c.localScale.y) && c.localPosition.y > 1f)
                ceil = Mathf.Min(ceil, c.localPosition.y - Mathf.Abs(c.localScale.y) * 0.5f);
        }
        return ceil;
    }

    private static bool Inside(List<Bounds> solids, Vector3 p)
    {
        for (int i = 0; i < solids.Count; i++) if (solids[i].Contains(p)) return true;
        return false;
    }

    // floor cells (half a unit square) of the new strip at normal floor height that nothing covers yet
    private static int FillFloor(Mod m, Transform tile, List<Transform> kids, Transform sample, Material mat, float edge, float D, float width,
        List<Bounds> obs, List<Bounds> addedBounds, ref int skipped)
    {
        int filled = 0;
        foreach (Transform c in kids)
        {
            if (c.name != "1Topblock" || c.localPosition.y >= 1f || ZMin(c) > edge + 0.6f) continue;
            float floorTop = c.localPosition.y + Mathf.Abs(c.localScale.y) * 0.5f;
            if (floorTop < -0.3f || floorTop > 0.3f) continue;          // pits (lava, water) keep their own shape
            float fx0 = c.localPosition.x - Mathf.Abs(c.localScale.x) * 0.5f, fx1 = c.localPosition.x + Mathf.Abs(c.localScale.x) * 0.5f;
            if (width > 0f) { fx0 = Mathf.Max(fx0, 0f); fx1 = Mathf.Min(fx1, width); }
            int cells = Mathf.CeilToInt(D / 0.5f - 0.01f);
            for (float x = fx0 + 0.25f; x < fx1 - 0.1f; x += 0.5f)
                for (int k = 1; k <= cells; k++)
                {
                    float z0 = edge - 0.5f * k;
                    if (Inside(addedBounds, tile.TransformPoint(new Vector3(x, floorTop - 0.1f, z0 + 0.25f)))) continue;
                    Vector3 s = sample.localScale;
                    GameObject g = PlainBrick(sample, tile, mat, new Vector3(x, floorTop - 0.25f, z0), Quaternion.identity, new Vector3(s.x, s.y, s.z * 0.5f));
                    if (Keep(m, g, obs, addedBounds, ref skipped)) filled++;
                }
        }
        return filled;
    }

    // A tile that continues toward you past the new wall (a stairwell going down): its own solid
    // blocks (side locks, the passage ceiling) are drawn as bricks where they cross the new strip,
    // and the new wall is built around the passage: a wall cell stays open where the tile has a
    // ceiling above it and nothing solid in it, which is exactly the way down.
    private static int Doorway(Mod m, Transform tile, List<Transform> kids, Transform sample, Material mat, float edge, float D, float width, float top,
        List<Bounds> obs, List<Bounds> addedBounds, ref int skipped, ref int openings)
    {
        List<Bounds> solids = new List<Bounds>();
        foreach (Transform c in kids)
        {
            if (c.name != "1Frontblock" && c.name != "1sidelock" && c.name != "1Topblock") continue;
            BoxCollider bc = c.GetComponent<BoxCollider>();
            if (bc != null && !bc.isTrigger && bc.enabled) solids.Add(bc.bounds);
        }
        int placed = 0;
        int cells = Mathf.CeilToInt(D / 0.5f - 0.01f);
        float zw = edge - D - 0.25f;
        Quaternion up = Quaternion.Euler(-90f, 0f, 0f);
        for (float x = 0.25f; x < width - 0.1f; x += 0.5f)
            for (float y = -0.5f; y < top - 0.05f; y += 1f)
            {
                for (int k = 1; k <= cells; k++)
                {
                    float zc = edge - 0.5f * k + 0.25f;
                    if (!Inside(solids, tile.TransformPoint(new Vector3(x, y + 0.5f, zc)))) continue;
                    if (Keep(m, PlainBrick(sample, tile, mat, new Vector3(x, y, zc), up, sample.localScale), obs, addedBounds, ref skipped)) placed++;
                }
                bool inside = Inside(solids, tile.TransformPoint(new Vector3(x, y + 0.5f, zw)));
                bool roofed = false;
                for (float yy = y + 0.75f; yy <= top + 1f && !roofed; yy += 0.25f)
                    if (Inside(solids, tile.TransformPoint(new Vector3(x, yy, zw)))) roofed = true;
                if (!inside && roofed) { openings++; continue; }
                if (Keep(m, PlainBrick(sample, tile, mat, new Vector3(x, y, zw), up, sample.localScale), obs, addedBounds, ref skipped)) placed++;
            }
        return placed;
    }

    // the room's torches (a Light under a Fireswitch): census, and their reach into the new strip
    private static string Torches(Mod m, Transform room, float edge, float D, List<Bounds> addedBounds)
    {
        Light[] ls = room.GetComponentsInChildren<Light>(true);
        int torches = 0, lit = 0, front = 0, buried = 0, other = 0, otherLit = 0;
        float fMin = float.MaxValue, fMax = 0f;
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < ls.Length; i++)
        {
            Light l = ls[i];
            if (l == null) continue;
            bool on = l.enabled && l.gameObject.activeInHierarchy;
            if (l.GetComponentInParent<Fireswitch>() == null) { other++; if (on) otherLit++; continue; }
            torches++;
            if (on) lit++;
            for (int k = 0; k < addedBounds.Count; k++) if (addedBounds[k].Contains(l.transform.position)) { buried++; break; }
            // tile-local depth of the torch
            Transform tile = l.transform;
            while (tile.parent != null && tile.parent != room) tile = tile.parent;
            float dz = tile.InverseTransformPoint(l.transform.position).z - edge;
            if (dz < 0.75f) { front++; continue; }
            if (!FPConfig.RoomLightReach || l.type != LightType.Point) continue;
            float f = Mathf.Min(1.5f, (dz + D) / dz);
            m.lights.Add(l); m.origRange.Add(l.range); m.newRange.Add(l.range * f);
            l.range = l.range * f;
            fMin = Mathf.Min(fMin, f); fMax = Mathf.Max(fMax, f);
        }
        sb.Append("torches: " + torches + " (" + lit + " lit now, " + (torches - lit) + " dark until a switch lights them), " + front + " at the front edge");
        if (m.lights.Count > 0) sb.Append("; reach into the new strip: " + m.lights.Count + " back torches x" + fMin.ToString("0.00") + (fMax > fMin + 0.005f ? "-" + fMax.ToString("0.00") : "") + " range");
        else sb.Append(FPConfig.RoomLightReach ? "; no back torches to extend" : "; torch reach unchanged (RoomLightReach=false)");
        if (buried > 0) sb.Append("; WARNING " + buried + " torch lights sit inside new bricks");
        sb.Append("; other lights " + other + " (" + otherLit + " lit)");
        return sb.ToString();
    }

    private static void Apply(Mod m, bool on)
    {
        for (int i = 0; i < m.added.Count; i++) if (m.added[i] != null && m.added[i].activeSelf != on) m.added[i].SetActive(on);
        for (int i = 0; i < m.moved.Count; i++)
        {
            Transform t = m.moved[i];
            if (t == null) continue;
            t.localPosition = on ? m.newPos[i] : m.origPos[i];
            t.localScale = on ? m.newScale[i] : m.origScale[i];
        }
        for (int i = 0; i < m.lights.Count; i++) if (m.lights[i] != null) m.lights[i].range = on ? m.newRange[i] : m.origRange[i];
    }

    // The Tavern is built by hand for the tabletop camera: open at the front, no ceiling, its walls
    // and roof only invisible collision boxes (1Frontblock, 1Topblock). First person stands a
    // wall of bricks inside each of those boxes (running bond, whole and half bricks, the
    // Tavern's own brick materials in their proportions), wherever nothing visible is there
    // already. Inside a collision box, a brick can never block a way the game lets you walk, and
    // the gaps between boxes (the doorway, the stairs) stay open. No colliders are added. Shown
    // only while first person drives, like the deeper rooms.
    // bricks filling a thin box (a wall, a roof, a floor) in running bond, whole and half bricks,
    // skipping places where something visible is already; materials in the given proportions,
    // seeded by position. No colliders. Returns the number made, or -1 if the box is not thin.
    public static int FillBox(Bounds b, Transform r, Mesh mesh, MeshRenderer template, List<Material> mats, List<int> weights, int total, List<Bounds> obs, List<GameObject> added, out int covered)
    {
        covered = 0;
        Vector3 e = b.size;
        // thin axis t, long axis a, the other b2
        int t = e.x <= e.y && e.x <= e.z ? 0 : (e.y <= e.z ? 1 : 2);
        int a = -1, b2 = -1;
        for (int k = 0; k < 3; k++) { if (k == t) continue; if (a < 0 || e[k] > e[a]) { b2 = a; a = k; } else b2 = k; }
        if (b2 < 0) for (int k = 0; k < 3; k++) if (k != t && k != a) b2 = k;
        if (e[t] > 1.6f || e[a] < 0.45f || e[b2] < 0.45f) return -1;   // a volume, not a wall
        int rows = Mathf.Max(1, Mathf.FloorToInt(e[b2] / 0.5f + 0.25f));
        int halves = Mathf.Max(1, Mathf.FloorToInt(e[a] / 0.5f + 0.25f));
        Vector3 ea = Vector3.zero; ea[a] = 1f;
        Vector3 up = Vector3.zero; up[a == 1 ? t : 1] = 1f;
        Quaternion rot = Quaternion.LookRotation(ea, up);
        float aStart = b.center[a] - halves * 0.25f;
        float bStart = b.center[b2] - rows * 0.25f;
        int boxMade = 0;
        for (int row = 0; row < rows; row++)
        {
            // running bond: every other row starts with a half brick
            int pos = 0;
            bool first = true;
            while (pos < halves)
            {
                int len = (first && (row & 1) == 1) || pos == halves - 1 ? 1 : 2;
                first = false;
                Vector3 p = b.center;
                p[a] = aStart + pos * 0.5f;
                p[b2] = bStart + row * 0.5f + 0.25f;
                Vector3 ctr = p; ctr[a] += len * 0.25f;
                Vector3 size = new Vector3(0.5f, 0.5f, 0.5f); size[a] = len * 0.5f;
                pos += len;
                Bounds bb = new Bounds(ctr, size);
                bb.Expand(-0.12f);
                bool hit = false;
                for (int o = 0; o < obs.Count && !hit; o++) if (obs[o].Intersects(bb)) hit = true;
                if (hit) { covered++; continue; }
                // pick a material like the Tavern's own mix, seeded by position
                int seed = (Mathf.RoundToInt(ctr.x * 8f) * 73856093 ^ Mathf.RoundToInt(ctr.y * 8f) * 19349663 ^ Mathf.RoundToInt(ctr.z * 8f) * 83492791) & 0x7fffffff;
                int pick = seed % Mathf.Max(1, total), mi = 0;
                for (int q = 0; q < weights.Count; q++) { if (pick < weights[q]) { mi = q; break; } pick -= weights[q]; }
                GameObject g = new GameObject("SmallBlock");
                g.layer = template.gameObject.layer;
                Transform gt = g.transform;
                gt.SetParent(r, true);
                gt.position = p;
                gt.rotation = rot;
                Vector3 ls0 = r.lossyScale;
                gt.localScale = new Vector3(0.5f / Mathf.Max(0.001f, Mathf.Abs(ls0.x)), 0.5f / Mathf.Max(0.001f, Mathf.Abs(ls0.y)), len * 0.25f / Mathf.Max(0.001f, Mathf.Abs(ls0.z)));
                g.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer mr = g.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mats[mi];
                mr.shadowCastingMode = template.shadowCastingMode;
                mr.receiveShadows = template.receiveShadows;
                added.Add(g);
                obs.Add(bb);
                boxMade++;
            }
        }
        return boxMade;
    }

    private static void TavernWalls(GameObject room, Mod m)
    {
        System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
        Transform r = room.transform;
        Mesh mesh = null;
        Dictionary<Material, int> matCount = new Dictionary<Material, int>();
        MeshRenderer template = null;
        for (int i = 0; i < r.childCount; i++)
        {
            Transform c = r.GetChild(i);
            if (c.name != "SmallBlock") continue;
            MeshFilter mf = c.GetComponent<MeshFilter>();
            MeshRenderer mr = c.GetComponent<MeshRenderer>();
            if (mf == null || mr == null || mf.sharedMesh == null || mr.sharedMaterial == null) continue;
            if (mesh == null) { mesh = mf.sharedMesh; template = mr; }
            int n; matCount.TryGetValue(mr.sharedMaterial, out n); matCount[mr.sharedMaterial] = n + 1;
        }
        if (mesh == null) { Debug.Log("[FirstPersonLoD] VR room shape: Tavern has no bricks to copy; no walls added"); return; }
        List<Material> mats = new List<Material>(); List<int> weights = new List<int>(); int total = 0;
        foreach (KeyValuePair<Material, int> kv in matCount) { mats.Add(kv.Key); weights.Add(kv.Value); total += kv.Value; }
        // what is already visible (and the lights) must not be covered
        List<Bounds> obs = new List<Bounds>();
        Renderer[] rs = room.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] == null || rs[i].name == "FPBrick" || !(rs[i] is MeshRenderer)) continue;
            Vector3 sz = rs[i].bounds.size;
            if (sz.sqrMagnitude < 1e-6f) continue;
            obs.Add(rs[i].bounds);
        }
        Light[] ls = room.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < ls.Length; i++) if (ls[i] != null) obs.Add(new Bounds(ls[i].transform.position, Vector3.one * 0.4f));
        StringBuilder sb = new StringBuilder();
        int made = 0, covered = 0, boxes = 0;
        for (int i = 0; i < r.childCount && made < 1500; i++)
        {
            Transform c = r.GetChild(i);
            if (c.name != "1Frontblock" && c.name != "1Topblock") continue;
            BoxCollider bc = c.GetComponent<BoxCollider>();
            if (bc == null || bc.isTrigger) continue;
            Bounds b = bc.bounds;
            int cov;
            int boxMade = FillBox(b, r, mesh, template, mats, weights, total, obs, m.added, out cov);
            covered += cov;
            if (boxMade < 0) continue;
            boxes++;
            made += boxMade;
            Vector3 e = b.size;
            int t = e.x <= e.y && e.x <= e.z ? 0 : (e.y <= e.z ? 1 : 2);
            sb.Append(" " + c.name + "(" + "xyz"[t] + " " + e[t].ToString("0.##") + " thick, " + boxMade + ")");
        }
        Debug.Log("[FirstPersonLoD] VR room shape: Tavern walls and ceiling: " + made + " bricks in " + boxes + " invisible wall boxes, " + covered
            + " places left out (something visible there already) in " + w.ElapsedMilliseconds + " ms:" + sb);
        if (FPConfig.TavernTorches > 0) TavernTorches(room, m);
    }

    // Torches for the Tavern's corners (first person only): with walls and a ceiling the corners are
    // dark. The inside is taken from the invisible side walls (x-thin boxes: left, right, and their
    // front-to-back span) and the lowest ceiling box; a torch goes on each side wall near the front
    // and near the back (TavernTorches = how many per side, spread front to back), with a warm,
    // flickering light. The existing lights are listed in the log.
    private static void TavernTorches(GameObject room, Mod m)
    {
        Transform r = room.transform;
        Bounds left = new Bounds(), right = new Bounds(), ceil = new Bounds();
        bool haveL = false, haveR = false, haveC = false;
        for (int i = 0; i < r.childCount; i++)
        {
            Transform c = r.GetChild(i);
            if (c.name != "1Frontblock" && c.name != "1Topblock") continue;
            BoxCollider bc = c.GetComponent<BoxCollider>();
            if (bc == null || bc.isTrigger) continue;
            Bounds b = bc.bounds;
            Vector3 e = b.size;
            if (e.x < e.y && e.x < e.z && e.y >= 2f && e.z >= 2f)
            {
                if (!haveL || b.center.x < left.center.x) { left = b; haveL = true; }
                if (!haveR || b.center.x > right.center.x) { right = b; haveR = true; }
            }
            if (e.y < e.x && e.y < e.z && e.x >= 3f && (!haveC || b.center.y > ceil.center.y)) { ceil = b; haveC = true; }
        }
        StringBuilder ls = new StringBuilder();
        Light[] lights = room.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < lights.Length; i++)
        {
            Light l = lights[i];
            ls.Append("\n  " + l.name + " " + l.type + " at " + r.InverseTransformPoint(l.transform.position).ToString("F1") + " range " + l.range.ToString("0.#")
                + " intensity " + l.intensity.ToString("0.##") + " colour " + l.color.ToString("F2") + (l.enabled && l.gameObject.activeInHierarchy ? "" : " (off)") + " shadows " + l.shadows);
        }
        if (!haveL || !haveR || left.center.x >= right.center.x)
        {
            Debug.Log("[FirstPersonLoD] VR room shape: Tavern torches: side walls not found, none added; the Tavern's lights:" + ls);
            return;
        }
        float xL = left.max.x, xR = right.min.x;
        float z0 = Mathf.Max(left.min.z, right.min.z) + 0.9f, z1 = Mathf.Min(left.max.z, right.max.z) - 0.9f;
        float yTop = haveC ? ceil.min.y : Mathf.Min(left.max.y, right.max.y);
        float y = yTop - 0.95f;
        Shader wood = Shader.Find("Legacy Shaders/Bumped Diffuse") ?? Shader.Find("Standard");
        Shader glow = Shader.Find("Particles/Additive");
        Material woodMat = new Material(wood); woodMat.color = new Color(0.22f, 0.13f, 0.07f);
        Material flameMat = glow != null ? new Material(glow) : null;
        if (flameMat != null) { flameMat.mainTexture = Texture2D.whiteTexture; if (flameMat.HasProperty("_TintColor")) flameMat.SetColor("_TintColor", new Color(1f, 0.55f, 0.2f, 0.6f)); }
        int per = Mathf.Clamp(FPConfig.TavernTorches, 1, 4);
        int made = 0;
        for (int side = 0; side < 2; side++)
            for (int k = 0; k < per; k++)
            {
                float z = per == 1 ? (z0 + z1) * 0.5f : Mathf.Lerp(z0, z1, k / (float)(per - 1));
                float x = side == 0 ? xL + 0.12f : xR - 0.12f;
                float inward = side == 0 ? 1f : -1f;
                GameObject t = new GameObject("FPTorch");
                t.transform.SetParent(r, true);
                t.transform.position = new Vector3(x, y, z);
                // the stick leans out from the wall
                GameObject stick = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(stick.GetComponent<Collider>());
                stick.name = "FPTorchStick";
                stick.transform.SetParent(t.transform, false);
                stick.transform.localPosition = new Vector3(0.06f * inward, 0f, 0f);
                stick.transform.localRotation = Quaternion.Euler(0f, 0f, -22f * inward);
                stick.transform.localScale = new Vector3(0.07f, 0.34f, 0.07f);
                stick.GetComponent<MeshRenderer>().sharedMaterial = woodMat;
                GameObject flame = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(flame.GetComponent<Collider>());
                flame.name = "FPTorchFlame";
                flame.transform.SetParent(t.transform, false);
                flame.transform.localPosition = new Vector3(0.13f * inward, 0.2f, 0f);
                flame.transform.localScale = new Vector3(0.12f, 0.16f, 0.12f);
                MeshRenderer fr = flame.GetComponent<MeshRenderer>();
                if (flameMat != null) fr.sharedMaterial = flameMat;
                fr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                GameObject lg = new GameObject("FPTorchLight");
                lg.transform.SetParent(t.transform, false);
                lg.transform.localPosition = new Vector3(0.25f * inward, 0.25f, 0f);
                Light l = lg.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.62f, 0.3f);
                l.range = FPConfig.TavernTorchRange;
                l.intensity = FPConfig.TavernTorchIntensity;
                l.shadows = LightShadows.None;
                FPTorches.Add(l, flame.transform);
                m.added.Add(t);
                made++;
            }
        Debug.Log("[FirstPersonLoD] VR room shape: Tavern torches: " + made + " on the side walls (inside x " + r.InverseTransformPoint(new Vector3(xL, 0, 0)).x.ToString("0.0") + ".."
            + r.InverseTransformPoint(new Vector3(xR, 0, 0)).x.ToString("0.0") + ", front to back " + (z1 - z0 + 1.8f).ToString("0.0") + ", ceiling at " + (yTop - r.position.y).ToString("0.0")
            + "), range " + FPConfig.TavernTorchRange.ToString("0.#") + ", intensity " + FPConfig.TavernTorchIntensity.ToString("0.#") + "; the Tavern's own lights:" + ls);
    }

    // first person on: deeper rooms; tabletop (or menus): the game's own rooms
    public static void Show(bool on)
    {
        if (shown == on) return;
        shown = on;
        List<int> dead = new List<int>();
        foreach (KeyValuePair<int, Mod> kv in mods)
        {
            if (kv.Value.room == null) { dead.Add(kv.Key); continue; }
            Apply(kv.Value, on);
        }
        for (int i = 0; i < dead.Count; i++) mods.Remove(dead[i]);
    }
}

// ---------------------------------------------------------------------------------------------
// Inventory screen (first person). The game keeps one flat list of carried items and only lets
// you step through it one at a time. Hold the left bumper (Steam Frame) to open a panel in front
// of you with the items sorted into Health, Shield, Weapon, Magic and Misc, each shown with the
// game's own inventory icon, its name, stack or ammo count and hotkey A/B marks; the item in your
// hand is marked. Pick with the right-hand pointer and the trigger, or with the D-pad and A.
// B or the bumper closes without changing anything. The game is paused while the panel is open
// (InvPause) and none of your buttons reach it. Equipping goes through the game's own
// Inventory.SetItem, the same call its hotkeys use, so stats, stacks and ammo move exactly as in
// the original game.
// The D-pad has its own actions (InvLeft/Right/Up/Down). While the panel is closed they are passed
// to the game as previous/next item and hotkeys A/B, exactly like before.
// Hands (punch, high five: for levers, gates and switches) have their own tab, one press of the
// left trigger from Health. Works in first person and in the game's tabletop view alike.
// Sorting: potions, bandages, food and drink = Health; shields and headwear = Shield; swung weapons
// and guns = Weapon; spells, staffs, summons = Magic; everything else = Misc (the log lists each
// item with its class when the panel opens).
// ---------------------------------------------------------------------------------------------
public static class InvMenu
{
    public static bool Open;
    private static float graceUntil;
    public static bool Blocking { get { return Open || Time.realtimeSinceStartup < graceUntil; } }
    public static string Note = "never opened";

    private const int NCat = 7;
    private const int cProj = 0, cWeapon = 1, cMagic = 2, cHealth = 3, cShield = 4, cMisc = 5, cHand = 6;
    private static readonly string[] Cats = { "Projectile", "Weapon", "Magic", "Health", "Shield", "Misc", "Hand" };
    private class Entry
    {
        public int index, cat, stack, ammo;
        public GameObject go;
        public string name = "", icon = "", stats = "";
        public bool held, hotA, hotB, hasUV;
        public string verb = "";     // what the second step offers besides holding it: Drink, Eat, Use, Wear
        public Rect uv;
        public float aspect = 1f;
    }
    private static readonly List<Entry>[] lists = { new List<Entry>(), new List<Entry>(), new List<Entry>(), new List<Entry>(), new List<Entry>(), new List<Entry>(), new List<Entry>() };
    private static Inventory inv;
    private static GameObject who;
    private static int tab, sel, scroll;
    private static bool onTabs;
    private static int hoverTab = -1, hoverCell = -1;
    // second step: [verb, Hold] for the picked item
    private static bool popup;
    private static Entry popEntry;
    private static int popSel, hoverBtn = -1;
    private static GameObject popRoot;
    private static Mesh[] btnMesh = new Mesh[2];
    private static TextMesh[] btnText = new TextMesh[2];
    private static TextMesh popTitle;
    private const float BtnW = 0.17f, BtnH = 0.07f, BtnY = -0.035f, BtnDX = 0.1f;
    // a Drink/Eat/Use/Wear chosen in the menu, carried out once the game runs again: take the item
    // in hand, press Use for you (the game's own use: drinking, eating, putting on), then give you
    // back what you were holding
    private static int stage;                   // 0 idle, 1 wait to press, 2 pressing, 3 wait for it to finish
    public static int InjectUse;                // read by Filter: 1 press this frame, 2 release next, 3 done
    private static GameObject pTarget, pPrev, pHat, pWho;
    private static int pStack;
    private static string pVerb = "";
    private static float pClosedAt, pT0;
    private static string footerNote = "";

    // input (raw OpenVR, so it works while the game's own input is blocked)
    private static readonly string[] Acts = { "InvLeft", "InvRight", "InvUp", "InvDown", "Inventory", "Use", "Jump", "Next", "Prev", "Hands" };
    private const int aL = 0, aR = 1, aU = 2, aD = 3, aInv = 4, aUse = 5, aA = 6, aB = 7, aLT = 8, aHands = 9;
    private static readonly ulong[] hs = new ulong[10];
    private static readonly bool[] now = new bool[10], was = new bool[10];
    private static float handsT;
    private static bool handsArmed = true;
    private static GameObject beforeHands;       // the item to go back to on the next hold
    private static float holdT;
    private static bool holdArmed = true, invReleased;
    private static float savedTimeScale = 1f;
    private static bool timePaused;

    // visuals
    private const int Cols = 5, Rows = 3;
    private static GameObject root, laser, dot;
    private static Transform space;
    private static Material solidMat, bgMat, topMat, iconMat, textMat, popMat, textTopMat, dotMat;
    private static readonly List<Mesh> meshes = new List<Mesh>();
    private static int lastTickFrame, lastHover = -1;
    private static Font font;
    private static float textUnit = -1f;          // TextMesh line height at characterSize 1, fontSize 64 (measured)
    private static Mesh[] tabMesh = new Mesh[NCat];
    private static TextMesh[] tabText = new TextMesh[NCat];
    private static Mesh[] cellMesh = new Mesh[Rows * Cols];
    private static GameObject[] iconGo = new GameObject[Rows * Cols];
    private static Mesh[] iconMesh = new Mesh[Rows * Cols];
    private static TextMesh[] cellName = new TextMesh[Rows * Cols], cellCount = new TextMesh[Rows * Cols], cellHot = new TextMesh[Rows * Cols];
    private static TextMesh title, stats, hint, emptyText;
    private static Mesh laserMesh;

    private const float TabY = 0.245f, TabW = 0.113f, TabH = 0.058f, TabDX = 0.121f;
    private const float CellW = 0.15f, CellH = 0.125f, CellDX = 0.165f, CellDY = 0.135f, GridTop = 0.135f;
    private const float PanelW = 0.88f, PanelH = 0.64f;

    private static bool Dig(int i)
    {
        Valve.VR.CVRInput input = Valve.VR.OpenVR.Input;
        if (input == null) return false;
        if (hs[i] == 0) input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/" + Acts[i], ref hs[i]);
        if (hs[i] == 0) return false;
        Valve.VR.InputDigitalActionData_t d = new Valve.VR.InputDigitalActionData_t();
        input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ hs[i], ref d, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputDigitalActionData_t)), 0);
        return d.bActive && d.bState;
    }
    private static bool Down(int i) { return now[i] && !was[i]; }

    // ---- the D-pad for the game while the panel is closed; everything blocked while it is open ----
    private static readonly ulong[] ph = new ulong[4];
    private static bool useLatch, jumpLatch;
    public static void Filter(VRInputs vi)
    {
        if (vi == null) return;
        if (Blocking)
        {
            vi.Move = Vector2.zero;
            vi.Jump = vi.JumpDN = vi.JumpUP = vi.Use = vi.UseDN = vi.UseUP = false;
            vi.Up = vi.Down = vi.Left = vi.Right = vi.UpDN = vi.DownDN = vi.LeftDN = vi.RightDN = false;
            vi.Prev = vi.Next = vi.Drop = vi.HotA = vi.HotB = vi.HotAUP = vi.HotBUP = vi.Scr = vi.Menu = false;
            Sword6.Inject = 0;
            if (InjectUse == 1) InjectUse = 0;
            else if (InjectUse == 2 || releaseOwed) { vi.UseUP = true; InjectUse = 3; releaseOwed = false; }
            return;
        }
        if (releaseOwed) { vi.UseUP = true; releaseOwed = false; }
        // a trigger or A still held from picking an item must not attack or jump when it is let go
        if (useLatch) { if (!vi.Use && !vi.UseDN) useLatch = false; vi.Use = vi.UseDN = vi.UseUP = false; }
        if (jumpLatch) { if (!vi.Jump && !vi.JumpDN) jumpLatch = false; vi.Jump = vi.JumpDN = vi.JumpUP = false; }
        // a use chosen in the inventory: one press, released the next frame, like a quick trigger pull
        if (InjectUse == 1) { vi.Use = true; vi.UseDN = true; InjectUse = 2; }
        else if (InjectUse == 2) { vi.Use = false; vi.UseUP = true; InjectUse = 3; }
        Valve.VR.CVRInput input = Valve.VR.OpenVR.Input;
        if (input == null) return;
        uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.InputDigitalActionData_t));
        for (int i = 0; i < 4; i++)
        {
            if (ph[i] == 0) input.AAAAAAAAAAAAAAAAAuq( /* GetActionHandle */ "/actions/VR_Controls/in/" + Acts[i], ref ph[i]);
            if (ph[i] == 0) continue;
            Valve.VR.InputDigitalActionData_t d = new Valve.VR.InputDigitalActionData_t();
            input.AAAAAAAAAAAAAAAAAAAAwt( /* GetDigitalActionData */ ph[i], ref d, size, 0);
            if (!d.bActive) continue;
            bool rise = d.bState && d.bChanged, fall = !d.bState && d.bChanged;
            if (i == aL && rise) vi.Prev = true;
            else if (i == aR && rise) vi.Next = true;
            else if (i == aU) { if (d.bState) vi.HotA = true; if (fall) vi.HotAUP = true; }
            else if (i == aD) { if (d.bState) vi.HotB = true; if (fall) vi.HotBUP = true; }
        }
    }

    // called every frame from the mod's update: a panel whose Tick stopped running (first person lost
    // without a release) must not leave the game paused with its input blocked
    public static void Watchdog()
    {
        if (Open && Time.frameCount - lastTickFrame > 3) Close("inventory closed: first person stopped updating");
    }

    // ---- per frame while first person drives ----
    public static void Tick(GameObject player, Transform head)
    {
        if (!FPConfig.InvMenu || player == null || head == null || head.parent == null) { if (Open) Close("unavailable"); CancelPending("inventory unavailable"); return; }
        for (int i = 0; i < Acts.Length; i++) { was[i] = now[i]; now[i] = Dig(i); }
        if (!Open)
        {
            Pending(player);
            // right bumper held: bare hands; held again: back to what you had
            // Frame: right bumper; Touch: right grip, held longer (a grip squeezed while holding a
            // weapon, a habit from other games, should not swap it for bare hands; 0 = off)
            bool frameHands = OffHandUse.IsFrame;
            float handsHold = frameHands ? FPConfig.InvHoldSeconds : FPConfig.TouchHandsHold;
            if (now[aHands] && handsHold > 0f)
            {
                handsT += Time.unscaledDeltaTime;
                if (handsArmed && handsT >= handsHold) { handsArmed = false; HandsToggle(player); }
            }
            else { handsT = 0f; handsArmed = true; }
            if (now[aInv])
            {
                holdT += Time.unscaledDeltaTime;
                if (holdArmed && holdT >= FPConfig.InvHoldSeconds) { holdArmed = false; OpenMenu(player, head); }
            }
            else { holdT = 0f; holdArmed = true; }
            return;
        }
        if (!now[aInv]) invReleased = true;
        if (inv == null || who != player) { Close("player changed"); return; }
        if (popup)
        {
            lastTickFrame = Time.frameCount;
            if (Down(aB)) { ShowPopup(false); Refresh(); return; }
            if (invReleased && Down(aInv)) { Close("closed"); return; }
            if (Down(aL)) popSel = 0;
            if (Down(aR)) popSel = 1;
            Pointer(head);
            if (hoverBtn >= 0) popSel = hoverBtn;
            if (Down(aUse) || Down(aA)) { Execute(popEntry, popSel == 0 ? popEntry.verb : "Hold"); return; }
            Refresh();
            return;
        }
        if ((invReleased && Down(aInv)) || Down(aB)) { Close("closed"); return; }
        if (Down(aHands)) { Close("closed for hands"); HandsToggle(player); handsArmed = false; return; }

        lastTickFrame = Time.frameCount;
        if (onTabs)
        {
            if (Down(aL)) SetTab(tab - 1);
            if (Down(aR)) SetTab(tab + 1);
            if (Down(aD) && lists[tab].Count > 0) { onTabs = false; was[aD] = true; }
        }
        int n = lists[tab].Count;
        if (!onTabs)
        {
            if (Down(aL) && sel > 0) sel--;
            if (Down(aR) && sel < n - 1) sel++;
            if (Down(aD) && sel + Cols < n) sel += Cols;
            else if (Down(aD) && sel / Cols < (n - 1) / Cols) sel = n - 1;
            if (Down(aU)) { if (sel >= Cols) sel -= Cols; else onTabs = true; }
        }
        if (Down(aLT)) SetTab(tab - 1);
        Pointer(head);
        n = lists[tab].Count;
        if (Down(aUse))
        {
            if (hoverTab >= 0) { SetTab(hoverTab); onTabs = lists[tab].Count == 0; n = lists[tab].Count; }
            else if (hoverCell >= 0) { Pick(lists[tab][scroll * Cols + hoverCell]); return; }
            else if (!onTabs && sel < n) { Pick(lists[tab][sel]); return; }
        }
        if (Down(aA) && !onTabs && sel < n) { Pick(lists[tab][sel]); return; }
        if (sel >= n) sel = Mathf.Max(0, n - 1);
        int row = sel / Cols;
        if (row < scroll) scroll = row;
        if (row >= scroll + Rows) scroll = row - Rows + 1;
        Refresh();
    }

    private static void HandsToggle(GameObject player)
    {
        Inventory iv = player.GetComponentInChildren<Inventory>();
        if (iv == null || iv.Stuff == null || iv.Stuff.Count == 0) return;
        Reflect();
        GameObject cur = iv.index >= 0 && iv.index < iv.Stuff.Count ? iv.Stuff[iv.index] : null;
        int target = -1;
        string what;
        bool curIsHand = cur != null && IsHand(ItemName(cur).ToLowerInvariant() + " " + cur.name.ToLowerInvariant());
        if (curIsHand && beforeHands != null && FPConfig.HandsToggleBack)
        {
            target = iv.Stuff.IndexOf(beforeHands);
            what = "back to " + (target >= 0 ? ItemName(beforeHands) : "?");
            beforeHands = null;
        }
        else
        {
            // fists first, then any other hand item (high five)
            for (int pass = 0; pass < 2 && target < 0; pass++)
                for (int i = 0; i < iv.Stuff.Count && target < 0; i++)
                {
                    GameObject g = iv.Stuff[i];
                    if (g == null) continue;
                    string n = (ItemName(g) + " " + g.name).ToLowerInvariant();
                    if (pass == 0 ? n.Contains("punch") : IsHand(n)) target = i;
                }
            if (target >= 0 && !curIsHand) beforeHands = cur;
            what = "hands";
        }
        if (target < 0 || target == iv.index) { Debug.Log("[FirstPersonLoD] VR hands button: nothing to switch to (" + what + ")"); return; }
        string why = Switch(iv, player, target);
        Debug.Log("[FirstPersonLoD] VR hands button: " + what + (why.Length > 0 ? " not possible: " + why : ""));
    }

    private static string ItemName(GameObject g)
    {
        DropWhat dw = g.GetComponent<DropWhat>();
        try { if (dw != null && mName != null) { string s = mName.Invoke(dw, null) as string; if (!string.IsNullOrEmpty(s)) return s; } } catch (Exception) { }
        return CleanName(g);
    }

    // the game's own item switch (what its hotkeys call); "" = done, otherwise why not
    private static string Switch(Inventory iv, GameObject player, int index)
    {
        PlayerMovement2 pm = player.GetComponent<PlayerMovement2>();
        if (pm != null && pm.chargeing) return "Let go of your charged attack first";
        if (mSetItem == null) return "Could not reach the game's item switch";
        try
        {
            mSetItem.Invoke(iv, new object[] { index });
            if (iv.itemswitch != null) AudioSource.PlayClipAtPoint(iv.itemswitch, player.transform.position);
            return "";
        }
        catch (Exception ex)
        {
            Debug.LogError("[FirstPersonLoD] VR inventory: item switch failed: " + ex);
            return "Switch failed";
        }
    }

    private static void SetTab(int t)
    {
        tab = (t % NCat + NCat) % NCat;
        sel = 0; scroll = 0;
        footerNote = "";
        for (int i = 0; i < lists[tab].Count; i++) if (lists[tab][i].held) sel = i;
    }

    // ---- reading the game's inventory ----
    private static MethodInfo mSetItem, mName, mGetSprite;
    private static FieldInfo fA, fB, fHot;
    private static bool reflected;
    private static void Reflect()
    {
        if (reflected) return;
        reflected = true;
        BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        mSetItem = typeof(Inventory).GetMethod("SetItem", any, null, new Type[] { typeof(int) }, null)
            ?? typeof(Inventory).GetMethod("AAAAAAAAAAAAAAAAAAAAAii", any, null, new Type[] { typeof(int) }, null);
        mName = typeof(DropWhat).GetMethod("Name", any, null, Type.EmptyTypes, null)
            ?? typeof(DropWhat).GetMethod("AAAAAAAAAAAAAAAAAAAAAgu", any, null, Type.EmptyTypes, null);
        mGetSprite = typeof(UIAtlas).GetMethod("GetSprite", any, null, new Type[] { typeof(string) }, null)
            ?? typeof(UIAtlas).GetMethod("AAAAAAAAAAAAAAAAAAAAAeb", any, null, new Type[] { typeof(string) }, null);
        fA = typeof(Inventory).GetField("A", any);
        fB = typeof(Inventory).GetField("B", any);
        fHot = typeof(Inventory).GetField("HotKeys", any) ?? typeof(Inventory).GetField("AAAAAAAAAAAAAAAAAim", any);
        Debug.Log("[FirstPersonLoD] VR inventory: game calls " + (mSetItem != null ? "SetItem ok" : "SetItem MISSING") + ", " + (mName != null ? "names ok" : "names missing")
            + ", " + (mGetSprite != null ? "icons ok" : "icons missing") + ", hotkeys " + (fA != null && fB != null && fHot != null ? "ok" : "missing"));
    }

    private static bool Has(Component[] cs, string type)
    {
        for (int i = 0; i < cs.Length; i++) if (cs[i] != null && cs[i].GetType().Name == type) return true;
        return false;
    }

    private static int Classify(GameObject g, string name)
    {
        Component[] cs = g.GetComponentsInChildren<MonoBehaviour>(true);
        string n = (name + " " + g.name).ToLowerInvariant();
        // bare hands (punch, high five): what you need for levers, gates and switches
        if (IsHand(n)) return cHand;
        if (n.Contains("shiruken") || n.Contains("shuriken")) return cProj;    // thrown stars use the potion-throw code
        if (Has(cs, "PotionColorize") || Has(cs, "PotionItem") || Has(cs, "Heal") || Has(cs, "PotionStatEffect")
            || n.Contains("potion") || n.Contains("bandage") || n.Contains("apple") || n.Contains("beer")) return cHealth;
        if (n.Contains("shield") || Has(cs, "WearHat")) return cShield;
        if (Has(cs, "MagicMissle") || Has(cs, "VineMagic") || Has(cs, "ConfuseMissle") || n.Contains("summon") || n.Contains("staff")) return cMagic;
        if (n.Contains("lantern") || n.Contains("remote") || n.Contains("wrench") || Has(cs, "SprayCan") || Has(cs, "TakeOffHat")) return cMisc;
        if (n.Contains("chainsaw")) return cWeapon;                              // a gun in the code, a melee weapon in the hand
        if (Has(cs, "Gun")) return cProj;
        if (Has(cs, "SwingItem")) return cWeapon;
        return cMisc;
    }

    // consumables and wearables get a second step (use it now, or just hold it)
    private static string Verb(GameObject g, Entry e)
    {
        Component[] cs = g.GetComponentsInChildren<MonoBehaviour>(true);
        string n = (e.name + " " + g.name).ToLowerInvariant();
        if (Has(cs, "WearHat")) return "Wear";
        if (e.cat != cHealth) return "";
        if (n.Contains("apple") || n.Contains("food") || n.Contains("meat") || n.Contains("cake")) return "Eat";
        if (n.Contains("bandage")) return "Use";
        return "Drink";
    }

    private static bool IsHand(string lowerName)
    {
        return lowerName.Contains("punch") || lowerName.StartsWith("pet") || lowerName.Contains("highfive") || lowerName.Contains("high five");
    }

    private static string CleanName(GameObject g)
    {
        string s = g.name.Replace("(Clone)", "").Replace("Hold", "").Replace("Pickup", "").Trim();
        return s.Length > 0 ? s : g.name;
    }

    private static void Read()
    {
        for (int c = 0; c < NCat; c++) lists[c].Clear();
        Reflect();
        Guid ga = Guid.Empty, gb = Guid.Empty;
        List<Guid> hot = null;
        try
        {
            if (fA != null) ga = (Guid)fA.GetValue(inv);
            if (fB != null) gb = (Guid)fB.GetValue(inv);
            if (fHot != null) hot = fHot.GetValue(inv) as List<Guid>;
        }
        catch (Exception) { }
        UIAtlas atlas = null;
        try { if (inv.nslot3 != null) atlas = inv.nslot3.atlas; } catch (Exception) { }
        Texture atex = null;
        bool pixels = true;
        try { if (atlas != null) { atex = atlas.texture; pixels = atlas.coordinates == UIAtlas.Coordinates.Pixels; } } catch (Exception) { }
        if (atex != null && iconMat != null) iconMat.mainTexture = atex;
        StringBuilder log = new StringBuilder();
        int icons = 0;
        for (int i = 0; inv.Stuff != null && i < inv.Stuff.Count; i++)
        {
            GameObject g = inv.Stuff[i];
            if (g == null) continue;
            Entry e = new Entry();
            e.index = i; e.go = g;
            DropWhat dw = g.GetComponent<DropWhat>();
            try { if (dw != null && mName != null) e.name = mName.Invoke(dw, null) as string ?? ""; } catch (Exception) { }
            if (e.name.Length == 0) e.name = CleanName(g);
            if (dw != null && dw.iconname != null) e.icon = dw.iconname;
            ItemStat st = g.GetComponent<ItemStat>();
            if (st != null)
            {
                try { e.stack = st.Stack; } catch (Exception) { }
                e.ammo = st.Ammo;
                StringBuilder s = new StringBuilder();
                if (st.Str != 0) s.Append("Str " + (st.Str > 0 ? "+" : "") + st.Str + "   ");
                if (st.Def != 0) s.Append("Def " + (st.Def > 0 ? "+" : "") + st.Def + "   ");
                if (st.Speed != 0) s.Append("Speed " + (st.Speed > 0 ? "+" : "") + st.Speed + "   ");
                if (st.Jump != 0) s.Append("Jump " + (st.Jump > 0 ? "+" : "") + st.Jump + "   ");
                e.stats = s.ToString().Trim();
            }
            bool shooter = g.GetComponentsInChildren<Gun>(true).Length > 0 || g.GetComponentsInChildren<MagicMissle>(true).Length > 0;
            if (!shooter) e.ammo = -1;
            if (shooter && e.ammo >= 0) e.stats = ("Ammo " + e.ammo + "   " + e.stats).Trim();
            e.held = i == inv.index && inv.CurrentItem != null;
            if (hot != null && i < hot.Count) { e.hotA = ga != Guid.Empty && hot[i] == ga; e.hotB = gb != Guid.Empty && hot[i] == gb; }
            e.cat = Classify(g, e.name);
            e.verb = Verb(g, e);
            if (atlas != null && e.icon.Length > 0 && mGetSprite != null && atex != null)
            {
                try
                {
                    UIAtlas.Sprite sp = mGetSprite.Invoke(atlas, new object[] { e.icon }) as UIAtlas.Sprite;
                    if (sp != null && sp.outer.width > 0f && sp.outer.height > 0f)
                    {
                        Rect o = sp.outer;
                        if (pixels)
                        {
                            e.uv = new Rect(o.xMin / atex.width, 1f - o.yMax / atex.height, o.width / atex.width, o.height / atex.height);
                            e.aspect = o.width / o.height;
                        }
                        else
                        {
                            e.uv = o;
                            e.aspect = (o.width * atex.width) / Mathf.Max(0.0001f, o.height * atex.height);
                        }
                        e.hasUV = true; icons++;
                    }
                }
                catch (Exception) { }
            }
            lists[e.cat].Add(e);
            if (log.Length < 1500) log.Append((log.Length > 0 ? ", " : "") + e.name + "=" + Cats[e.cat] + (e.held ? "*" : ""));
        }
        Debug.Log("[FirstPersonLoD] VR inventory: " + (inv.Stuff != null ? inv.Stuff.Count : 0) + " items, " + icons + " icons from atlas " + (atex != null ? atex.name + " " + atex.width + "x" + atex.height : "none")
            + " (* = in hand): " + log);
    }

    // ---- open / close ----
    private static void OpenMenu(GameObject player, Transform head)
    {
        inv = player.GetComponentInChildren<Inventory>();
        if (inv == null) { Debug.Log("[FirstPersonLoD] VR inventory: no Inventory on " + player.name); return; }
        who = player;
        space = head.parent;
        try
        {
            Materials();
            Read();
            Build(head);
        }
        catch (Exception e)
        {
            Debug.LogError("[FirstPersonLoD] VR inventory: could not open: " + e);
            Destroy();
            return;
        }
        CancelPending("menu opened again");
        Open = true;
        invReleased = false;
        footerNote = "";
        lastHover = -1;
        lastTickFrame = Time.frameCount;
        onTabs = false;
        tab = cWeapon;
        for (int c = 0; c < NCat; c++) for (int i = 0; i < lists[c].Count; i++) if (lists[c][i].held) tab = c;
        SetTab(tab);
        if (lists[tab].Count == 0) onTabs = true;
        if (FPConfig.InvPause) { savedTimeScale = Time.timeScale; Time.timeScale = 0f; timePaused = true; }
        Note = "opened";
        Refresh();
    }

    public static void Close(string why)
    {
        popup = false;
        if (!Open && root == null) return;
        Open = false;
        graceUntil = Time.realtimeSinceStartup + 0.3f;
        useLatch = jumpLatch = true;
        if (timePaused) { Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f; timePaused = false; }
        Destroy();
        Note = why;
        Debug.Log("[FirstPersonLoD] VR inventory: " + why);
    }

    private static void Destroy()
    {
        if (root != null) UnityEngine.Object.Destroy(root);
        if (laser != null) UnityEngine.Object.Destroy(laser);
        for (int i = 0; i < meshes.Count; i++) if (meshes[i] != null) UnityEngine.Object.Destroy(meshes[i]);
        meshes.Clear();
        root = null; laser = null; dot = null;
    }

    private static void Pick(Entry e)
    {
        if (e.verb.Length == 0) { Equip(e); return; }
        popEntry = e;
        popSel = 0;
        ShowPopup(true);
        Refresh();
    }

    private static void ShowPopup(bool on)
    {
        popup = on;
        hoverBtn = -1;
        if (popRoot == null) return;
        popRoot.SetActive(on);
        if (!on || popEntry == null) return;
        popTitle.text = Short(popEntry.name, 30);
        btnText[0].text = popEntry.verb;
        btnText[1].text = "Hold";
    }

    private static void Execute(Entry e, string action)
    {
        if (action == "Hold") { Equip(e); return; }
        if (inv.Stuff == null || e.index >= inv.Stuff.Count || inv.Stuff[e.index] != e.go) { footerNote = "Your items changed; reopen the inventory"; ShowPopup(false); return; }
        GameObject cur = inv.index >= 0 && inv.index < inv.Stuff.Count && inv.CurrentItem != null ? inv.Stuff[inv.index] : null;
        if (!e.held)
        {
            string why = Switch(inv, who, e.index);
            if (why.Length > 0) { footerNote = why; ShowPopup(false); return; }
        }
        beforeHands = null;
        pTarget = e.go; pPrev = e.held ? null : cur; pHat = inv.CurrentHat; pWho = who; pStack = e.stack; pVerb = action;
        stage = 1;
        Close(action + " " + e.name + (pPrev != null ? " (then back to " + ItemName(pPrev) + ")" : ""));
        pClosedAt = Time.realtimeSinceStartup;
    }

    private static void CancelPending(string why)
    {
        if (stage != 0) Debug.Log("[FirstPersonLoD] VR inventory: " + pVerb + " cancelled (" + why + ")");
        stage = 0;
        if (InjectUse == 1) InjectUse = 0;
        else if (InjectUse == 2) releaseOwed = true;   // a press went out: its release must still follow
    }
    private static bool releaseOwed;

    private static void Pending(GameObject player)
    {
        if (stage == 0) return;
        float now2 = Time.realtimeSinceStartup;
        if (player != pWho || pWho == null) { CancelPending("player changed"); return; }
        if (now2 - pClosedAt > 8f) { CancelPending("took too long"); return; }
        Inventory iv = pWho.GetComponentInChildren<Inventory>();
        if (iv == null) { CancelPending("no inventory"); return; }
        bool targetInHand = pTarget != null && iv.Stuff != null && iv.index >= 0 && iv.index < iv.Stuff.Count && iv.Stuff[iv.index] == pTarget && iv.CurrentItem != null;
        if (stage == 1)
        {
            if (pTarget == null || (!targetInHand && !Blocking && now2 - pClosedAt >= 0.4f && iv.CurrentItem != null)) { CancelPending("you switched items first"); return; }
            // the game needs a moment after the switch (the new item starts up) and our input block ends
            if (Blocking || now2 - pClosedAt < 0.4f || iv.CurrentItem == null) { if (now2 - pClosedAt > 3f) { stage = 0; Debug.Log("[FirstPersonLoD] VR inventory: " + pVerb + " gave up: the item never came into your hand"); } return; }
            InjectUse = 1; stage = 2; pT0 = now2;
            return;
        }
        if (stage == 2) { if (InjectUse == 3 || now2 - pT0 > 1f) { if (InjectUse < 3 && InjectUse > 0) CancelPending("press did not go out"); else { stage = 3; pT0 = now2; } } return; }
        // stage 3: finished once the item is used up (gone or one fewer) or worn (a different hat)
        bool gone = pTarget == null || iv.Stuff == null || !iv.Stuff.Contains(pTarget);
        int stack = 0;
        if (!gone) { ItemStat st = pTarget.GetComponent<ItemStat>(); if (st != null) { try { stack = st.Stack; } catch (Exception) { } } }
        bool used = gone || (pStack > 0 && stack < pStack) || iv.CurrentHat != pHat;
        if (!used && now2 - pT0 < 4f) return;
        stage = 0;
        string note = pVerb + (used ? " done" : " did not finish within 4 s");
        // go back only if nothing else was chosen meanwhile (the used item is gone or still in hand)
        if (FPConfig.ReturnAfterUse && pPrev != null && iv.Stuff != null && (gone || targetInHand))
        {
            int back = iv.Stuff.IndexOf(pPrev);
            if (back >= 0 && back != iv.index)
            {
                string why = Switch(iv, pWho, back);
                note += why.Length > 0 ? "; could not go back: " + why : "; back to " + ItemName(pPrev);
            }
        }
        Debug.Log("[FirstPersonLoD] VR inventory: " + note);
    }

    private static void Equip(Entry e)
    {
        ShowPopup(false);
        beforeHands = null;
        string what = e.name + " (" + Cats[e.cat] + ")";
        if (e.held) { Close("kept " + what); return; }
        if (inv.Stuff == null || e.index >= inv.Stuff.Count || inv.Stuff[e.index] != e.go) { footerNote = "Your items changed; reopen the inventory"; return; }
        string why = Switch(inv, who, e.index);
        if (why.Length > 0) { footerNote = why; return; }
        Close("equipped " + what);
    }

    // ---- drawing ----
    private static void Materials()
    {
        if (solidMat == null)
        {
            Shader s = Shader.Find("GUI/Text Shader") ?? Shader.Find("Unlit/Transparent");
            solidMat = new Material(s);
            solidMat.mainTexture = Texture2D.whiteTexture;
            solidMat.renderQueue = 4001;          // tabs and cells, over the backdrop
            bgMat = new Material(solidMat); bgMat.renderQueue = 4000;
            popMat = new Material(solidMat); popMat.renderQueue = 4004;   // second-step panel over the grid and its text
            topMat = new Material(solidMat); topMat.renderQueue = 4005;   // pointer beam and the second step's buttons
            dotMat = new Material(solidMat); dotMat.renderQueue = 4007;   // pointer dot, over everything
        }
        if (iconMat == null)
        {
            Shader s = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
            iconMat = new Material(s);
            iconMat.SetFloat("unity_GUIZTestMode", (float)UnityEngine.Rendering.CompareFunction.Always);
            iconMat.renderQueue = 4002;
            iconMat.SetVector("_ClipRect", new Vector4(-100000f, -100000f, 100000f, 100000f));
            Debug.Log("[FirstPersonLoD] VR inventory: panel shader " + solidMat.shader.name + ", icon shader " + s.name);
        }
        if (font == null) font = Resources.GetBuiltinResource(typeof(Font), "Arial.ttf") as Font;
        if (textMat == null && font != null) { textMat = new Material(font.material); textMat.renderQueue = 4003; }
        if (textTopMat == null && font != null) { textTopMat = new Material(font.material); textTopMat.renderQueue = 4006; }
    }

    private static Mesh QuadMesh(float w, float h, Color c)
    {
        Mesh m = new Mesh();
        m.vertices = new Vector3[] { new Vector3(-w / 2, -h / 2, 0), new Vector3(w / 2, -h / 2, 0), new Vector3(-w / 2, h / 2, 0), new Vector3(w / 2, h / 2, 0) };
        m.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        m.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
        m.colors = new Color[] { c, c, c, c };
        m.RecalculateBounds();
        m.bounds = new Bounds(Vector3.zero, new Vector3(w, h, 0.05f) * 4f);
        meshes.Add(m);
        return m;
    }

    private static void Tint(Mesh m, Color c) { m.colors = new Color[] { c, c, c, c }; }

    private static GameObject Piece(string name, Transform parent, Vector3 pos, Mesh mesh, Material mat, List<Mesh> keep)
    {
        GameObject g = new GameObject(name);
        g.layer = 0;
        g.transform.SetParent(parent, false);
        g.transform.localPosition = pos;
        g.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer r = g.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        return g;
    }

    private static TextMesh Text(string name, Transform parent, Vector3 pos, float height, TextAnchor anchor, Color c)
    {
        GameObject g = new GameObject(name);
        g.layer = 0;
        g.transform.SetParent(parent, false);
        g.transform.localPosition = pos;
        TextMesh t = g.AddComponent<TextMesh>();
        t.font = font;
        t.fontSize = 64;
        t.characterSize = 1f;
        t.anchor = anchor;
        t.alignment = anchor == TextAnchor.MiddleLeft || anchor == TextAnchor.UpperLeft || anchor == TextAnchor.LowerLeft ? TextAlignment.Left
            : anchor == TextAnchor.MiddleRight || anchor == TextAnchor.UpperRight || anchor == TextAnchor.LowerRight ? TextAlignment.Right : TextAlignment.Center;
        t.color = c;
        MeshRenderer r = g.GetComponent<MeshRenderer>();
        if (textMat != null) r.sharedMaterial = textMat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        if (textUnit < 0f)
        {
            // one-time calibration: how tall a line is at this font size, in the panel's own units
            t.text = "Hg";
            float wy = r.bounds.size.y, ls = Mathf.Abs(g.transform.lossyScale.y);
            textUnit = wy > 0.0001f && ls > 0.0001f ? wy / ls : 6.4f;
            Debug.Log("[FirstPersonLoD] VR inventory: text line height " + textUnit.ToString("0.###") + " units at size 64" + (wy > 0.0001f ? "" : " (not measurable, assumed)"));
        }
        float k = height / textUnit;
        g.transform.localScale = new Vector3(k, k, k);
        t.text = "";
        return t;
    }

    private static Vector2 TabPos(int i) { return new Vector2((i - (NCat - 1) * 0.5f) * TabDX, TabY); }
    private static Vector2 CellPos(int k) { return new Vector2(-2f * CellDX + (k % Cols) * CellDX, GridTop - (k / Cols) * CellDY); }

    private static void Build(Transform head)
    {
        Destroy();
        root = new GameObject("FPInventory");
        root.transform.SetParent(space, false);
        Vector3 hp = head.localPosition;
        Vector3 f = head.localRotation * Vector3.forward; f.y = 0f;
        if (f.sqrMagnitude < 0.0001f) f = Vector3.forward;
        f.Normalize();
        root.transform.localPosition = hp + f * FPConfig.InvDistance + Vector3.down * 0.12f;
        root.transform.localRotation = Quaternion.LookRotation(f, Vector3.up) * Quaternion.Euler(12f, 0f, 0f);  // tilted slightly toward you
        root.transform.localScale = Vector3.one * FPConfig.InvScale;
        Transform R = root.transform;
        List<Mesh> keep = new List<Mesh>();
        Piece("bg", R, new Vector3(0, 0, 0.002f), QuadMesh(PanelW, PanelH, new Color(0.05f, 0.045f, 0.07f, 0.94f)), bgMat, keep);
        for (int i = 0; i < NCat; i++)
        {
            Vector2 p = TabPos(i);
            tabMesh[i] = QuadMesh(TabW, TabH, Color.gray);
            GameObject g = Piece("tab" + i, R, new Vector3(p.x, p.y, 0.001f), tabMesh[i], solidMat, keep);
            g.GetComponent<MeshRenderer>().sharedMaterial = solidMat;
            tabText[i] = Text("tabtext" + i, R, new Vector3(p.x, p.y, 0f), 0.017f, TextAnchor.MiddleCenter, Color.white);
        }
        for (int k = 0; k < Rows * Cols; k++)
        {
            Vector2 p = CellPos(k);
            cellMesh[k] = QuadMesh(CellW, CellH, Color.gray);
            Piece("cell" + k, R, new Vector3(p.x, p.y, 0.001f), cellMesh[k], solidMat, keep);
            iconMesh[k] = QuadMesh(1f, 1f, Color.white);
            iconGo[k] = Piece("icon" + k, R, new Vector3(p.x, p.y + 0.014f, 0f), iconMesh[k], iconMat, keep);
            cellName[k] = Text("name" + k, R, new Vector3(p.x, p.y - 0.046f, 0f), 0.0165f, TextAnchor.MiddleCenter, new Color(0.92f, 0.9f, 0.85f));
            cellCount[k] = Text("count" + k, R, new Vector3(p.x + CellW / 2 - 0.006f, p.y + CellH / 2 - 0.004f, 0f), 0.017f, TextAnchor.UpperRight, new Color(1f, 0.85f, 0.4f));
            cellHot[k] = Text("hot" + k, R, new Vector3(p.x - CellW / 2 + 0.006f, p.y + CellH / 2 - 0.004f, 0f), 0.017f, TextAnchor.UpperLeft, new Color(0.55f, 0.85f, 1f));
        }
        emptyText = Text("empty", R, new Vector3(0f, 0.02f, 0f), 0.026f, TextAnchor.MiddleCenter, new Color(0.7f, 0.68f, 0.75f));
        title = Text("title", R, new Vector3(-PanelW / 2 + 0.03f, -0.215f, 0f), 0.03f, TextAnchor.MiddleLeft, Color.white);
        stats = Text("stats", R, new Vector3(-PanelW / 2 + 0.03f, -0.252f, 0f), 0.019f, TextAnchor.MiddleLeft, new Color(0.8f, 0.78f, 0.7f));
        hint = Text("hint", R, new Vector3(PanelW / 2 - 0.03f, -0.285f, 0f), 0.015f, TextAnchor.MiddleRight, new Color(0.6f, 0.58f, 0.66f));
        hint.text = OffHandUse.IsFrame ? "Trigger or A: pick     D-pad: move     Left trigger: previous tab     B: close     Right bumper (hold): hands"
            : "Trigger or A: pick (point at it)     Left trigger: previous tab     B: close     Right grip (hold): hands";

        // second step (drink / eat / wear, or just hold it), drawn over the grid
        popRoot = new GameObject("popup");
        popRoot.transform.SetParent(R, false);
        popRoot.transform.localPosition = new Vector3(0f, 0.01f, -0.004f);
        Piece("popbg", popRoot.transform, Vector3.zero, QuadMesh(0.48f, 0.21f, new Color(0.08f, 0.07f, 0.11f, 0.97f)), popMat, keep);
        popTitle = Text("poptitle", popRoot.transform, new Vector3(0f, 0.052f, 0f), 0.026f, TextAnchor.MiddleCenter, Color.white);
        popTitle.GetComponent<MeshRenderer>().sharedMaterial = textTopMat;
        for (int i = 0; i < 2; i++)
        {
            btnMesh[i] = QuadMesh(BtnW, BtnH, Color.gray);
            Piece("btn" + i, popRoot.transform, new Vector3((i == 0 ? -1f : 1f) * BtnDX, BtnY, 0f), btnMesh[i], topMat, keep);
            btnText[i] = Text("btntext" + i, popRoot.transform, new Vector3((i == 0 ? -1f : 1f) * BtnDX, BtnY, 0f), 0.026f, TextAnchor.MiddleCenter, Color.white);
            btnText[i].GetComponent<MeshRenderer>().sharedMaterial = textTopMat;
        }
        popRoot.SetActive(false);
        popup = false;

        // pointer: a thin beam from the right controller and a dot where it meets the panel
        laserMesh = QuadMesh(1f, 1f, new Color(0.55f, 0.85f, 1f, 0.8f));
        laser = new GameObject("FPInventoryBeam");
        laser.transform.SetParent(space, false);
        laser.AddComponent<MeshFilter>().sharedMesh = laserMesh;
        MeshRenderer lr = laser.AddComponent<MeshRenderer>();
        lr.sharedMaterial = topMat;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        dot = Piece("dot", R, Vector3.zero, QuadMesh(0.012f, 0.012f, new Color(0.7f, 0.95f, 1f, 1f)), dotMat, keep);
        dot.SetActive(false);
        laser.SetActive(false);
    }

    private static void Pointer(Transform head)
    {
        hoverTab = hoverCell = hoverBtn = -1;
        PointerInner(head);
        if (hoverCell < 0) lastHover = -1;
    }

    private static void PointerInner(Transform head)
    {
        if (root == null) return;
        Vector3 hp; Quaternion hr;
        if (!VRHand.Get(true, out hp, out hr)) { laser.SetActive(false); dot.SetActive(false); return; }
        Vector3 d = hr * Quaternion.Euler(FPConfig.InvPointerPitch, 0f, 0f) * Vector3.forward;
        Transform R = root.transform;
        // into panel space (the panel and the controller pose share the tracking space)
        Quaternion inv = Quaternion.Inverse(R.localRotation);
        float sc = Mathf.Max(0.0001f, R.localScale.x);
        Vector3 lo = inv * (hp - R.localPosition) / sc, ld = inv * d;
        float len = 1.2f;
        bool hit = false;
        Vector3 q = Vector3.zero;
        if (ld.z > 0.0001f && lo.z < 0f)
        {
            float t = -lo.z / ld.z;
            q = lo + ld * t;
            if (Mathf.Abs(q.x) <= PanelW / 2 && Mathf.Abs(q.y) <= PanelH / 2) { hit = true; len = t * sc; }
        }
        // beam: a thin quad from the hand along the ray, turned to face the eyes
        Vector3 a = hp, b = hp + d * len, mid = (a + b) * 0.5f;
        Vector3 eye = head.localPosition;
        Vector3 side = Vector3.Cross(d, eye - mid);
        if (side.sqrMagnitude < 1e-8f) side = Vector3.Cross(d, Vector3.up);
        side.Normalize();
        // quad: local y along the beam, its face turned toward the eyes
        laser.SetActive(true);
        laser.transform.localPosition = mid;
        laser.transform.localRotation = Quaternion.LookRotation(Vector3.Cross(side, d), d);
        laser.transform.localScale = new Vector3(0.003f, len, 1f);
        dot.SetActive(hit);
        if (!hit) return;
        dot.transform.localPosition = new Vector3(q.x, q.y, -0.006f);
        if (popup)
        {
            for (int i = 0; i < 2; i++)
            {
                float bx = (i == 0 ? -1f : 1f) * BtnDX, by = BtnY + 0.01f;
                if (Mathf.Abs(q.x - bx) <= BtnW / 2 && Mathf.Abs(q.y - by) <= BtnH / 2) { hoverBtn = i; return; }
            }
            return;
        }
        for (int i = 0; i < NCat; i++)
        {
            Vector2 p = TabPos(i);
            if (Mathf.Abs(q.x - p.x) <= TabW / 2 && Mathf.Abs(q.y - p.y) <= TabH / 2) { hoverTab = i; return; }
        }
        int n = lists[tab].Count;
        for (int k = 0; k < Rows * Cols; k++)
        {
            int idx = scroll * Cols + k;
            if (idx >= n) break;
            Vector2 p = CellPos(k);
            if (Mathf.Abs(q.x - p.x) <= CellW / 2 && Mathf.Abs(q.y - p.y) <= CellH / 2)
            {
                hoverCell = k;
                if (idx != lastHover) { sel = idx; onTabs = false; lastHover = idx; }
                return;
            }
        }
    }

    private static string Short(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 1) + "."; }

    private static void Refresh()
    {
        if (root == null) return;
        for (int i = 0; i < NCat; i++)
        {
            bool cur = i == tab, focus = onTabs && cur, hov = i == hoverTab;
            Tint(tabMesh[i], focus ? new Color(0.85f, 0.6f, 0.2f) : cur ? new Color(0.55f, 0.37f, 0.12f) : hov ? new Color(0.3f, 0.27f, 0.38f) : new Color(0.17f, 0.15f, 0.21f));
            tabText[i].text = Cats[i] + "  " + lists[i].Count;
        }
        List<Entry> L = lists[tab];
        for (int k = 0; k < Rows * Cols; k++)
        {
            int idx = scroll * Cols + k;
            bool has = idx < L.Count;
            Entry e = has ? L[idx] : null;
            bool focus = has && !onTabs && idx == sel;
            Color bg = !has ? new Color(0.09f, 0.085f, 0.11f)
                : e.held ? (focus ? new Color(0.32f, 0.55f, 0.3f) : new Color(0.16f, 0.3f, 0.16f))
                : focus ? new Color(0.38f, 0.33f, 0.52f) : new Color(0.13f, 0.12f, 0.17f);
            Tint(cellMesh[k], bg);
            iconGo[k].SetActive(has && e.hasUV);
            if (has && e.hasUV)
            {
                iconMesh[k].uv = new Vector2[] { new Vector2(e.uv.xMin, e.uv.yMin), new Vector2(e.uv.xMax, e.uv.yMin), new Vector2(e.uv.xMin, e.uv.yMax), new Vector2(e.uv.xMax, e.uv.yMax) };
                float s = 0.07f;
                iconGo[k].transform.localScale = e.aspect >= 1f ? new Vector3(s, s / e.aspect, 1f) : new Vector3(s * e.aspect, s, 1f);
            }
            cellName[k].text = has ? Short(e.name, 17) : "";
            cellCount[k].text = has ? (e.ammo >= 0 ? e.ammo.ToString() : e.stack > 1 ? "x" + e.stack : "") : "";
            cellHot[k].text = has ? ((e.hotA ? "A" : "") + (e.hotB ? "B" : "")) : "";
        }
        emptyText.text = L.Count == 0 ? "Nothing in " + Cats[tab] : "";
        if (popup)
            for (int i = 0; i < 2; i++)
                Tint(btnMesh[i], i == popSel ? new Color(0.85f, 0.6f, 0.2f) : i == hoverBtn ? new Color(0.35f, 0.3f, 0.45f) : new Color(0.2f, 0.18f, 0.26f));
        Entry s2 = !onTabs && sel < L.Count ? L[sel] : null;
        title.text = s2 != null ? s2.name + (s2.held ? "   (in hand)" : "") : Cats[tab] + (L.Count > Rows * Cols ? "   (more below)" : "");
        stats.text = footerNote.Length > 0 ? footerNote : s2 != null ? s2.stats + (s2.hotA || s2.hotB ? "   hotkey " + (s2.hotA ? "A" : "B") : "") : "";
        if (L.Count > (scroll + Rows) * Cols && s2 != null) title.text += "   more below";
    }
}

// ---------------------------------------------------------------------------------------------
// Torch watch (diagnostics). The game switches torches only through Fireswitch.Flick: floor
// switches light every switchable torch within 10 units for 10 s and then switch all of them off
// (including torches that were lit before), and wall levers toggle them. Every change is logged
// with the torch, where it is, how far from you, and which game code switched it, so a report of
// torches going dark can be matched to its cause.
// ---------------------------------------------------------------------------------------------
public static class TorchWatch
{
    private static int logged, offs, ons;

    public static string Install()
    {
        MethodInfo f = typeof(Fireswitch).GetMethod("Flick", BindingFlags.Public | BindingFlags.Instance);
        if (f == null) return "Fireswitch.Flick not found";
        string e = HarmonyShim.Prefix(f, typeof(TorchWatch).GetMethod("FlickPrefix", BindingFlags.Public | BindingFlags.Static));
        return e == null ? "applied" : e;
    }

    public static string Counts() { return ons + " on, " + offs + " off"; }

    public static void FlickPrefix(Fireswitch __instance, bool __0)
    {
        try
        {
            if (!FPConfig.TorchLog || __instance == null || __instance.L == null) return;
            bool was = __instance.L.gameObject.activeSelf;
            if (was == __0) return;
            if (__0) ons++; else offs++;
            if (logged++ >= 300) return;
            StringBuilder by = new StringBuilder();
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
            for (int i = 0; i < st.FrameCount && by.Length < 160; i++)
            {
                MethodBase mb = st.GetFrame(i).GetMethod();
                if (mb == null || mb.DeclaringType == null) continue;
                string n = mb.DeclaringType.Name;
                if (n == "TorchWatch" || n.IndexOf("DMD") >= 0 || n.IndexOf("Harmony") >= 0 || (n == "Fireswitch" && mb.Name == "Flick")) continue;
                by.Append((by.Length > 0 ? " < " : "") + n + "." + mb.Name);
            }
            Transform t = __instance.transform;
            string path = t.name;
            for (Transform p = t.parent; p != null && path.Length < 90; p = p.parent) path = p.name + "/" + path;
            GameObject pl = VRFP.Player;
            Debug.Log("[FirstPersonLoD] VR torch " + (__0 ? "LIT" : "OFF") + ": " + path + " at " + t.position.ToString("F1")
                + (pl != null ? " (" + Vector3.Distance(pl.transform.position, t.position).ToString("0.0") + " from you)" : "")
                + " by " + (by.Length > 0 ? by.ToString() : "?") + "  [session: " + Counts() + "]");
        }
        catch (Exception) { }
    }
}

// ---------------------------------------------------------------------------------------------
// StoneCore: the pure arithmetic of the HD stone overlay (no Unity types, so it can run on a
// worker thread and be tested outside the game). Pixel arrays are RGBA bytes, rows bottom-up as
// Unity stores them. Coordinates in "texels" are the source texture's pixels; T output pixels
// cover one source texel.
//   Layers   texture-independent detail, made once: edge wobble (warp), grain, sand, mottling,
//            colour drift, pores/pits and hairline cracks, as an albedo multiplier and a height
//   Compose  one block texture: each source texel keeps its colour (the game's palette and
//            pattern), its square edges become irregular stone edges, the detail is multiplied
//            in, and a normal map (the game's bevels plus the new relief) and a height map for
//            parallax are made to match
// ---------------------------------------------------------------------------------------------
public static class StoneCore
{
    public sealed class Layers
    {
        public int W, H, T;
        public sbyte[] warpX, warpY, tint;
        public byte[] mul;       // albedo multiplier, (mul/255)+0.4
        public short[] height;   // detail height in output pixels, x256
    }

    private static uint Hash(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + seed * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
    private static float Rnd(int x, int y, uint seed) { return (Hash(x, y, seed) & 0xFFFFFF) * (2f / 16777215f) - 1f; }
    private static int Floor(float v) { int i = (int)v; return v < i ? i - 1 : i; }

    private static float Noise(float x, float y, uint seed)
    {
        int xi = Floor(x), yi = Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        float a = Rnd(xi, yi, seed), b = Rnd(xi + 1, yi, seed), c = Rnd(xi, yi + 1, seed), d = Rnd(xi + 1, yi + 1, seed);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    private static float Fbm(float x, float y, int oct, uint seed)
    {
        float s = 0f, amp = 1f, norm = 0f;
        for (int i = 0; i < oct; i++)
        {
            s += amp * Noise(x, y, seed + (uint)i * 101u);
            norm += amp; amp *= 0.5f; x = x * 2.03f + 17.1f; y = y * 2.03f + 5.3f;
        }
        return s / norm;
    }

    private sealed class Rng
    {
        private uint s;
        public Rng(uint seed) { s = seed * 747796405u + 2891336453u; if (s == 0) s = 1; }
        public float Next() { unchecked { s ^= s << 13; s ^= s >> 17; s ^= s << 5; } return (s & 0xFFFFFF) / 16777216f; }
    }

    // pores/pits and cracks: stamped depth maps (0..1)
    private static void Pits(float[] pit, int W, int H, int T, Rng r)
    {
        float sc = T / 32f;
        int count = (int)((long)W * H / (T * T) * 0.9f);
        for (int n = 0; n < count; n++)
        {
            float cx = r.Next() * W, cy = r.Next() * H;
            float u = r.Next();
            float rad = (1.0f + 2.6f * u * u) * sc;
            if (r.Next() < 0.05f) rad = (3f + 3.5f * r.Next()) * sc;
            float depth = 0.45f + 0.55f * r.Next();
            int x0 = Floor(cx - rad), x1 = Floor(cx + rad) + 1, y0 = Floor(cy - rad), y1 = Floor(cy + rad) + 1;
            for (int y = y0; y <= y1; y++)
            {
                if (y < 0 || y >= H) continue;
                for (int x = x0; x <= x1; x++)
                {
                    if (x < 0 || x >= W) continue;
                    float dx = (x + 0.5f - cx) / rad, dy = (y + 0.5f - cy) / rad;
                    float q = dx * dx + dy * dy;
                    if (q >= 1f) continue;
                    float v = depth * (1f - q) * (1f - q);
                    int i = y * W + x;
                    if (v > pit[i]) pit[i] = v;
                }
            }
        }
    }

    private static void Crack(float[] crack, int W, int H, int T, Rng r, float x, float y, float ang, float len, float width, int depthLeft)
    {
        float step = 0.7f;
        int steps = (int)(len / step);
        int branchAt = depthLeft > 0 && r.Next() < 0.35f ? (int)(steps * (0.25f + 0.5f * r.Next())) : -1;
        for (int s = 0; s < steps; s++)
        {
            float t = s / (float)steps;
            float taper = Math.Min(1f, Math.Min(t * 4f, (1f - t) * 3f));
            float w = width * (0.35f + 0.65f * taper);
            int x0 = Floor(x - w - 1), x1 = Floor(x + w + 1), y0 = Floor(y - w - 1), y1 = Floor(y + w + 1);
            for (int yy = y0; yy <= y1; yy++)
            {
                if (yy < 0 || yy >= H) continue;
                for (int xx = x0; xx <= x1; xx++)
                {
                    if (xx < 0 || xx >= W) continue;
                    float dx = xx + 0.5f - x, dy = yy + 0.5f - y;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    float v = 1f - d / (w + 0.6f);
                    if (v <= 0f) continue;
                    v *= 0.55f + 0.45f * taper;
                    int i = yy * W + xx;
                    if (v > crack[i]) crack[i] = v;
                }
            }
            ang += (r.Next() * 2f - 1f) * 0.32f;
            x += (float)Math.Cos(ang) * step; y += (float)Math.Sin(ang) * step;
            if (s == branchAt)
                Crack(crack, W, H, T, r, x, y, ang + (r.Next() < 0.5f ? 1f : -1f) * (0.5f + 0.6f * r.Next()), len * (0.3f + 0.3f * r.Next()), width * 0.7f, depthLeft - 1);
        }
    }

    // rough-hewn faces: the surface is the lower envelope of randomly tilted planes, one per
    // jittered cell, so it breaks into flat facets with sharp ridges like split or chiselled stone
    private static float[] Facets(int W, int H, int T, uint seed)
    {
        float C = T * 1.7f;                     // cell size in output pixels
        int gw = (int)(W / C) + 5, gh = (int)(H / C) + 5;
        int nc = gw * gh;
        float[] cx = new float[nc], cy = new float[nc], sx = new float[nc], sy = new float[nc], a = new float[nc];
        Rng r = new Rng(seed);
        float slope = 0.12f;
        for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
            {
                int k = j * gw + i;
                cx[k] = (i - 2 + 0.15f + 0.7f * r.Next()) * C; cy[k] = (j - 2 + 0.15f + 0.7f * r.Next()) * C;
                sx[k] = (r.Next() * 2f - 1f) * slope; sy[k] = (r.Next() * 2f - 1f) * slope;
                a[k] = (r.Next() * 2f - 1f) * C * 0.06f;
            }
        float[] h = new float[W * H];
        for (int y = 0; y < H; y++)
        {
            int cj = (int)(y / C) + 2;
            for (int x = 0; x < W; x++)
            {
                int ci = (int)(x / C) + 2;
                float best = float.MaxValue;
                for (int dj = -2; dj <= 2; dj++)
                    for (int di = -2; di <= 2; di++)
                    {
                        int ii = ci + di, jj = cj + dj;
                        if (ii < 0 || jj < 0 || ii >= gw || jj >= gh) continue;
                        int k = jj * gw + ii;
                        float dx = x - cx[k], dy = y - cy[k];
                        // a plane that falls away from its own centre, so each cell owns its facet
                        float v = a[k] + dx * sx[k] + dy * sy[k] + (float)Math.Sqrt(dx * dx + dy * dy) * 0.24f;
                        if (v < best) best = v;
                    }
                h[y * W + x] = -best * 0.5f;
            }
        }
        return h;
    }

    public static Layers MakeLayers(int W, int H, int T, uint seed)
    {
        Layers L = new Layers();
        L.W = W; L.H = H; L.T = T;
        int n = W * H;
        L.warpX = new sbyte[n]; L.warpY = new sbyte[n]; L.tint = new sbyte[n];
        L.mul = new byte[n]; L.height = new short[n];
        float[] pit = new float[n], crack = new float[n];
        Rng r = new Rng(seed);
        Pits(pit, W, H, T, r);
        float sc = T / 32f;
        int cracks = Math.Max(1, (int)((long)W * H / (T * T) / 22));
        for (int c = 0; c < cracks; c++)
            Crack(crack, W, H, T, r, r.Next() * W, r.Next() * H, r.Next() * 6.2832f, T * (0.5f + 1.7f * r.Next()), (0.45f + 0.5f * r.Next()) * sc, 1);
        float[] facet = Facets(W, H, T, seed + 31u);
        float wS = 1f / (T * 1.3f), wF = 1f / (T * 0.33f), gS = 1f / (8f * sc), fS = 1f / (1.5f * sc), mS = 1f / (T * 2.2f), tS = 1f / (T * 1.6f);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                // edge wobble, in texels: broad bends plus small chips
                float wx = 0.62f * Fbm(x * wS, y * wS, 3, seed + 11u) + 0.16f * Noise(x * wF, y * wF, seed + 12u);
                float wy = 0.62f * Fbm(x * wS + 31.7f, y * wS + 9.2f, 3, seed + 13u) + 0.16f * Noise(x * wF + 3.3f, y * wF + 8.1f, seed + 14u);
                float g = Fbm(x * gS, y * gS, 4, seed + 21u);
                float f = Noise(x * fS, y * fS, seed + 22u);
                float m = Fbm(x * mS, y * mS, 2, seed + 23u);
                float t = Fbm(x * tS + 71f, y * tS + 13f, 2, seed + 24u);
                float p = pit[i], k = crack[i];
                float mulv = (1f + 0.08f * g + 0.04f * f + 0.08f * m) * (1f - 0.22f * p) * (1f - 0.45f * k);
                float h = facet[i] + 0.55f * g + 0.12f * f + 0.6f * m - 2.2f * p - 2.4f * k;
                L.warpX[i] = (sbyte)Clamp(wx / 0.6f * 127f, -127f, 127f);
                L.warpY[i] = (sbyte)Clamp(wy / 0.6f * 127f, -127f, 127f);
                L.tint[i] = (sbyte)Clamp(t * 2.2f * 127f, -127f, 127f);
                L.mul[i] = (byte)Clamp((mulv - 0.4f) * 255f, 0f, 255f);
                L.height[i] = (short)Clamp(h * sc * 256f, -32000f, 32000f);
            }
        return L;
    }

    private static float Clamp(float v, float a, float b) { return v < a ? a : (v > b ? b : v); }

    // bilinear between texel centres with the blend squeezed into a narrow band: flat texel colour
    // inside, a soft edge 1/k texel wide. Clamped at the texture border.
    private static void Sharp(byte[] src, int sw, int sh, float sx, float sy, float k, out float r, out float g, out float b, out float a)
    {
        float u = sx - 0.5f, v = sy - 0.5f;
        int i0 = Floor(u), j0 = Floor(v);
        float fx = Clamp((u - i0 - 0.5f) * k + 0.5f, 0f, 1f), fy = Clamp((v - j0 - 0.5f) * k + 0.5f, 0f, 1f);
        int i1 = i0 + 1, j1 = j0 + 1;
        if (i0 < 0) i0 = 0; if (i0 >= sw) i0 = sw - 1; if (i1 < 0) i1 = 0; if (i1 >= sw) i1 = sw - 1;
        if (j0 < 0) j0 = 0; if (j0 >= sh) j0 = sh - 1; if (j1 < 0) j1 = 0; if (j1 >= sh) j1 = sh - 1;
        int p00 = (j0 * sw + i0) * 4, p10 = (j0 * sw + i1) * 4, p01 = (j1 * sw + i0) * 4, p11 = (j1 * sw + i1) * 4;
        float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        r = src[p00] * w00 + src[p10] * w10 + src[p01] * w01 + src[p11] * w11;
        g = src[p00 + 1] * w00 + src[p10 + 1] * w10 + src[p01 + 1] * w01 + src[p11 + 1] * w11;
        b = src[p00 + 2] * w00 + src[p10 + 2] * w10 + src[p01 + 2] * w01 + src[p11 + 2] * w11;
        a = src[p00 + 3] * w00 + src[p10 + 3] * w10 + src[p01 + 3] * w01 + src[p11 + 3] * w11;
    }

    // plain bilinear, clamped; returns the chosen channel (0..255)
    private static float Bilin(byte[] t, int w, int h, float sx, float sy, int ch)
    {
        float u = sx - 0.5f, v = sy - 0.5f;
        int i0 = Floor(u), j0 = Floor(v);
        float fx = u - i0, fy = v - j0;
        int i1 = i0 + 1, j1 = j0 + 1;
        if (i0 < 0) i0 = 0; if (i0 >= w) i0 = w - 1; if (i1 < 0) i1 = 0; if (i1 >= w) i1 = w - 1;
        if (j0 < 0) j0 = 0; if (j0 >= h) j0 = h - 1; if (j1 < 0) j1 = 0; if (j1 >= h) j1 = h - 1;
        float a = t[(j0 * w + i0) * 4 + ch], b = t[(j0 * w + i1) * 4 + ch], c = t[(j1 * w + i0) * 4 + ch], d = t[(j1 * w + i1) * 4 + ch];
        return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
    }

    // Wood (crates): the game's planks keep their colours and pattern; long grain streaks run along
    // the texture's u, with fine fibres and the odd darker line, and a matching normal map.
    public static string ComposeWood(byte[] src, int sw, int sh, int W, int H, int T,
        byte[] nrm, int nw, int nh, bool nrmAG, float strength, byte[] alb, byte[] nrmOut, float[] hbuf)
    {
        float s = strength;
        float kSharp = T / 3f;
        uint seed = 777u;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float sx = (x + 0.5f) / T, sy = (y + 0.5f) / T;
                float r, g, b, a;
                Sharp(src, sw, sh, sx, sy, kSharp, out r, out g, out b, out a);
                float grain = Fbm(x * 0.010f, y * 0.22f, 3, seed);
                float fibre = Noise(x * 0.05f, y * 0.9f, seed + 5u);
                float line = Math.Max(0f, 1f - Math.Abs(Fbm(x * 0.004f + 3f, y * 0.06f, 2, seed + 9u)) * 14f);
                float m = 1f + s * (0.16f * grain + 0.06f * fibre - 0.22f * line);
                int o = (y * W + x) * 4;
                alb[o] = (byte)Clamp(r * m, 0f, 255f); alb[o + 1] = (byte)Clamp(g * m * 0.98f, 0f, 255f); alb[o + 2] = (byte)Clamp(b * m * 0.95f, 0f, 255f);
                alb[o + 3] = (byte)Clamp(a, 0f, 255f);
                float lum = (0.3f * r + 0.55f * g + 0.15f * b) / 255f;
                hbuf[y * W + x] = s * (1.4f * grain + 0.4f * fibre - 1.6f * line) + lum * 3f;
            }
        Normals(hbuf, W, H, T, 0f, 0f, sw, sh, nrm, nw, nh, nrmAG, nrmOut);
        return "wood";
    }

    // normal map from a height field (output pixels), laid over the game's own normal map
    private static void Normals(float[] hbuf, int W, int H, int T, float e0x, float e0y, int sw, int sh, byte[] nrm, int nw, int nh, bool nrmAG, byte[] nrmOut)
    {
        for (int y = 0; y < H; y++)
        {
            int ym = y > 0 ? y - 1 : y, yp = y < H - 1 ? y + 1 : y;
            for (int x = 0; x < W; x++)
            {
                int xm = x > 0 ? x - 1 : x, xp = x < W - 1 ? x + 1 : x;
                float nx = -(hbuf[y * W + xp] - hbuf[y * W + xm]) * 0.5f;
                float ny = -(hbuf[yp * W + x] - hbuf[ym * W + x]) * 0.5f;
                float nz = 1f;
                if (nrm != null)
                {
                    float sx = e0x + (x + 0.5f) / T, sy = e0y + (y + 0.5f) / T;
                    float qx = sx / sw * nw, qy = sy / sh * nh;
                    float ox = Bilin(nrm, nw, nh, qx, qy, nrmAG ? 3 : 0) / 127.5f - 1f;
                    float oy = Bilin(nrm, nw, nh, qx, qy, 1) / 127.5f - 1f;
                    float oz = (float)Math.Sqrt(Math.Max(0f, 1f - ox * ox - oy * oy));
                    nx = ox + nx; ny = oy + ny; nz = oz * nz;
                }
                float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= len; ny /= len; nz /= len;
                int o = (y * W + x) * 4;
                byte bx = (byte)Clamp((nx * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o] = bx; nrmOut[o + 1] = (byte)Clamp((ny * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o + 2] = (byte)Clamp((nz * 0.5f + 0.5f) * 255f + 0.5f, 0, 255); nrmOut[o + 3] = bx;
            }
        }
    }

    // Metal (the untextured iron blocks): a tileable N x N plate, light grey so the game's own colour
    // tints it: brushed streaks, mottled wear, pits and scratches, with a normal map
    public static void Metal(int N, byte[] alb, byte[] nrmOut)
    {
        float[] h = new float[N * N];
        Rng r = new Rng(424242u);
        float[] scratch = new float[N * N];
        for (int k = 0; k < 18; k++)
        {
            float x = r.Next() * N, y = r.Next() * N, ang = r.Next() * 6.2832f, len = N * (0.1f + 0.35f * r.Next());
            for (float t = 0; t < len; t += 0.7f)
            {
                int ix = ((int)(x + Math.Cos(ang) * t) % N + N) % N, iy = ((int)(y + Math.Sin(ang) * t) % N + N) % N;
                scratch[iy * N + ix] = 1f;
            }
        }
        float[] pit = new float[N * N];
        for (int k = 0; k < N * N / 180; k++)
        {
            float cx = r.Next() * N, cy = r.Next() * N, rad = 0.8f + 2f * r.Next() * r.Next();
            for (int y = (int)(cy - rad) - 1; y <= (int)(cy + rad) + 1; y++)
                for (int x = (int)(cx - rad) - 1; x <= (int)(cx + rad) + 1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy, q = (dx * dx + dy * dy) / (rad * rad);
                    if (q >= 1f) continue;
                    int i = ((y % N + N) % N) * N + ((x % N + N) % N);
                    pit[i] = Math.Max(pit[i], (1f - q) * (1f - q));
                }
        }
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int i = y * N + x;
                // periodic noise: sample on a torus so the plate tiles
                float a = x * 6.2832f / N, b = y * 6.2832f / N;
                float brushed = Noise((float)Math.Cos(a) * 2f + 40f, y * 0.9f, 3u) * 0.5f + Noise((float)Math.Sin(a) * 2f + 70f, y * 0.9f, 4u) * 0.5f;
                float wear = Fbm((float)Math.Cos(a) * 1.3f + 11f, (float)Math.Sin(b) * 1.3f + (float)Math.Cos(b) * 0.7f + 5f, 3, 7u);
                float m = 0.86f + 0.07f * brushed + 0.09f * wear - 0.25f * pit[i] - 0.18f * scratch[i];
                byte v = (byte)Clamp(m * 235f, 0, 255);
                alb[i * 4] = v; alb[i * 4 + 1] = v; alb[i * 4 + 2] = (byte)Clamp(m * 240f, 0, 255); alb[i * 4 + 3] = 255;
                h[i] = 0.6f * brushed + 0.8f * wear - 2.2f * pit[i] - 1.2f * scratch[i];
            }
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float nx = -(h[y * N + (x + 1) % N] - h[y * N + (x + N - 1) % N]) * 0.5f;
                float ny = -(h[((y + 1) % N) * N + x] - h[((y + N - 1) % N) * N + x]) * 0.5f;
                float len = (float)Math.Sqrt(nx * nx + ny * ny + 1f);
                int o = (y * N + x) * 4;
                byte bx = (byte)Clamp((nx / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o] = bx; nrmOut[o + 1] = (byte)Clamp((ny / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o + 2] = (byte)Clamp((1f / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255); nrmOut[o + 3] = bx;
            }
    }

    // ---- carved statues ---------------------------------------------------------------------
    // The picture turned to stone, k output pixels per picture pixel (W = sw*k, H = sh*k). The
    // picture's shading stays (its "skin"), squeezed in contrast and mostly drained of colour
    // toward a warm grey, with stone on top: mottling, grain speckle, pits, a few hairline cracks.
    // Lines darker than their surroundings (eyes, feathers, folds) become carved grooves in the
    // normal map, darkened a little as grime settles in them. Alpha is solid: the carved mesh
    // decides the outline. alb/nrmOut: level 0 at the start.
    public static void StatueBake(byte[] src, int sw, int sh, int k, byte[] alb, byte[] nrmOut)
    {
        int W = sw * k, H = sh * k;
        byte[] dil = new byte[src.Length];
        Buffer.BlockCopy(src, 0, dil, 0, src.Length);
        Dilate(dil, sw, sh, 6);
        float[] L = new float[sw * sh], Lb = new float[sw * sh], tmp = new float[sw * sh];
        for (int i = 0; i < sw * sh; i++) L[i] = (0.3f * dil[i * 4] + 0.59f * dil[i * 4 + 1] + 0.11f * dil[i * 4 + 2]) / 255f;
        // surroundings: a 5 x 5 box, twice
        Buffer.BlockCopy(L, 0, Lb, 0, L.Length * 4);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
                { float s = 0; int n = 0; for (int d = -2; d <= 2; d++) { int xx = x + d; if (xx < 0 || xx >= sw) continue; s += Lb[y * sw + xx]; n++; } tmp[y * sw + x] = s / n; }
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
                { float s = 0; int n = 0; for (int d = -2; d <= 2; d++) { int yy = y + d; if (yy < 0 || yy >= sh) continue; s += tmp[yy * sw + x]; n++; } Lb[y * sw + x] = s / n; }
        }
        Rng r = new Rng(9173u + (uint)(sw * 31 + sh));
        float[] crack = new float[W * H], pit = new float[W * H];
        int cracks = Math.Max(2, sw * sh / 260);
        for (int c = 0; c < cracks; c++)
            Crack(crack, W, H, k, r, r.Next() * W, r.Next() * H, r.Next() * 6.2832f, k * (3f + 5f * r.Next()), Math.Max(0.5f, k * 0.035f), 2);
        int pits = W * H / Math.Max(1, k * k * 14);
        for (int p = 0; p < pits; p++)
        {
            float cx = r.Next() * W, cy = r.Next() * H, rad = k * (0.05f + 0.12f * r.Next() * r.Next()) + 0.6f;
            for (int y = (int)(cy - rad) - 1; y <= (int)(cy + rad) + 1; y++)
                for (int x = (int)(cx - rad) - 1; x <= (int)(cx + rad) + 1; x++)
                {
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy, q = (dx * dx + dy * dy) / (rad * rad);
                    if (q < 1f) { int i = y * W + x; pit[i] = Math.Max(pit[i], (1f - q) * (1f - q)); }
                }
        }
        float[] hb = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float sx = (x + 0.5f) / k, sy = (y + 0.5f) / k;
                float cr, cg, cb, ca;
                Sharp(dil, sw, sh, sx, sy, 2.5f, out cr, out cg, out cb, out ca);
                cr /= 255f; cg /= 255f; cb /= 255f;
                float l = 0.3f * cr + 0.59f * cg + 0.11f * cb;
                float lb = BilinF(Lb, sw, sh, sx, sy);
                float groove = Clamp((lb - l) * 2.4f - 0.04f, 0f, 1f);
                float grey = 0.36f + 0.64f * l;
                float br = grey * 0.95f, bg = grey * 0.93f, bb = grey * 0.88f;
                const float keep = 0.2f;
                br += (cr - l) * keep; bg += (cg - l) * keep; bb += (cb - l) * keep;
                float mott = Fbm(sx * 0.45f + 3.1f, sy * 0.45f + 7.7f, 4, 71u);
                float fine = Fbm(sx * 3.2f, sy * 3.2f, 3, 72u);
                uint hs = Hash(x, y, 73u) & 0xFFFF;
                float speck = hs < 700 ? 0.72f : (hs > 65535 - 500 ? 1.12f : 1f);
                int i = y * W + x;
                float m = (0.92f + 0.13f * mott + 0.06f * fine) * speck * (1f - 0.4f * crack[i]) * (1f - 0.3f * pit[i]) * (1f - 0.3f * groove);
                int o = i * 4;
                alb[o] = (byte)Clamp(br * m * 255f + 0.5f, 0, 255);
                alb[o + 1] = (byte)Clamp(bg * m * 255f + 0.5f, 0, 255);
                alb[o + 2] = (byte)Clamp(bb * m * 255f + 0.5f, 0, 255);
                alb[o + 3] = 255;
                // height in picture pixels
                hb[i] = -0.3f * groove + 0.07f * mott + 0.035f * fine - 0.18f * crack[i] - 0.16f * pit[i] + 0.1f * (l - lb);
            }
        // slope per picture pixel: neighbours are 2/k picture pixels apart
        float sc = k * 0.5f * 1.6f;
        for (int y = 0; y < H; y++)
        {
            int ym = y > 0 ? y - 1 : y, yp = y < H - 1 ? y + 1 : y;
            for (int x = 0; x < W; x++)
            {
                int xm = x > 0 ? x - 1 : x, xp = x < W - 1 ? x + 1 : x;
                float nx = -(hb[y * W + xp] - hb[y * W + xm]) * sc, ny = -(hb[yp * W + x] - hb[ym * W + x]) * sc;
                float len = (float)Math.Sqrt(nx * nx + ny * ny + 1f);
                int o = (y * W + x) * 4;
                byte bx = (byte)Clamp((nx / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o] = bx; nrmOut[o + 1] = (byte)Clamp((ny / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o + 2] = (byte)Clamp((1f / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255); nrmOut[o + 3] = bx;
            }
        }
    }

    private static float BilinF(float[] t, int w, int h, float sx, float sy)
    {
        float u = sx - 0.5f, v = sy - 0.5f;
        int i0 = Floor(u), j0 = Floor(v);
        float fx = u - i0, fy = v - j0;
        int i1 = i0 + 1, j1 = j0 + 1;
        if (i0 < 0) i0 = 0; if (i0 >= w) i0 = w - 1; if (i1 < 0) i1 = 0; if (i1 >= w) i1 = w - 1;
        if (j0 < 0) j0 = 0; if (j0 >= h) j0 = h - 1; if (j1 < 0) j1 = 0; if (j1 >= h) j1 = h - 1;
        return (t[j0 * w + i0] * (1 - fx) + t[j0 * w + i1] * fx) * (1 - fy) + (t[j1 * w + i0] * (1 - fx) + t[j1 * w + i1] * fx) * fy;
    }

    // The carved figure's shape on a grid U times finer than the picture: a cell is stone where the
    // picture's alpha, blended between pixel centres, is at least half (so pixel staircases become
    // smooth diagonals and outer corners round off a little). Each grid corner gets a height (in
    // picture pixels, each side of the middle): s0 at the outline, a 45 degree chamfer up to 65% of
    // D within 0.6 pixel, then a gentle rise to D where the figure is at least 4 pixels wide (the
    // body stands out from the wings). Corners not on any stone cell get -1.
    public static void CarveGrid(float[] alpha, int fw, int fh, int U, float D, out bool[] inside, out float[] hc, out int cells)
    {
        int GW = fw * U, GH = fh * U;
        inside = new bool[GW * GH];
        cells = 0;
        for (int j = 0; j < GH; j++)
            for (int i = 0; i < GW; i++)
            {
                float sx = (i + 0.5f) / U - 0.5f, sy = (j + 0.5f) / U - 0.5f;
                int i0 = Floor(sx), j0 = Floor(sy);
                float fx = sx - i0, fy = sy - j0;
                float a00 = A(alpha, fw, fh, i0, j0), a10 = A(alpha, fw, fh, i0 + 1, j0), a01 = A(alpha, fw, fh, i0, j0 + 1), a11 = A(alpha, fw, fh, i0 + 1, j0 + 1);
                float a = (a00 * (1 - fx) + a10 * fx) * (1 - fy) + (a01 * (1 - fx) + a11 * fx) * fy;
                if (a >= 0.5f) { inside[j * GW + i] = true; cells++; }
            }
        // distance (in cells) from each stone cell's centre to the nearest open cell's centre
        int PW = GW + 2, PH = GH + 2;
        float[] d = new float[PW * PH];
        const float INF = 1e9f, D1 = 1f, D2 = 1.4142f;
        for (int j = 0; j < PH; j++)
            for (int i = 0; i < PW; i++)
            {
                bool st = i > 0 && j > 0 && i <= GW && j <= GH && inside[(j - 1) * GW + (i - 1)];
                d[j * PW + i] = st ? INF : 0f;
            }
        for (int j = 1; j < PH - 1; j++)
            for (int i = 1; i < PW - 1; i++)
            {
                float v = d[j * PW + i]; if (v == 0f) continue;
                v = Math.Min(v, d[j * PW + i - 1] + D1); v = Math.Min(v, d[(j - 1) * PW + i] + D1);
                v = Math.Min(v, d[(j - 1) * PW + i - 1] + D2); v = Math.Min(v, d[(j - 1) * PW + i + 1] + D2);
                d[j * PW + i] = v;
            }
        for (int j = PH - 2; j >= 1; j--)
            for (int i = PW - 2; i >= 1; i--)
            {
                float v = d[j * PW + i]; if (v == 0f) continue;
                v = Math.Min(v, d[j * PW + i + 1] + D1); v = Math.Min(v, d[(j + 1) * PW + i] + D1);
                v = Math.Min(v, d[(j + 1) * PW + i + 1] + D2); v = Math.Min(v, d[(j + 1) * PW + i - 1] + D2);
                d[j * PW + i] = v;
            }
        hc = new float[(GW + 1) * (GH + 1)];
        float s0 = 0.2f * D;
        for (int gy = 0; gy <= GH; gy++)
            for (int gx = 0; gx <= GW; gx++)
            {
                // the four cells around this corner (padded grid: cell (i,j) sits at (i+1, j+1))
                float c00 = d[gy * PW + gx], c10 = d[gy * PW + gx + 1], c01 = d[(gy + 1) * PW + gx], c11 = d[(gy + 1) * PW + gx + 1];
                bool any = c00 > 0f || c10 > 0f || c01 > 0f || c11 > 0f;
                if (!any) { hc[gy * (GW + 1) + gx] = -1f; continue; }
                bool edge = c00 == 0f || c10 == 0f || c01 == 0f || c11 == 0f;
                if (edge) { hc[gy * (GW + 1) + gx] = s0; continue; }
                float m = Math.Min(Math.Min(c00, c10), Math.Min(c01, c11));
                float px = (m - 0.5f) / U;                 // picture pixels from the outline
                hc[gy * (GW + 1) + gx] = D * (0.2f + 0.45f * Math.Min(1f, px / 0.6f) + 0.35f * Clamp((px - 1.5f) / 2.5f, 0f, 1f));
            }
    }

    private static float A(float[] alpha, int fw, int fh, int i, int j)
    {
        if (i < 0 || j < 0 || i >= fw || j >= fh) return 0f;
        return alpha[j * fw + i];
    }

    // ---- crates -----------------------------------------------------------------------------
    // Crate boards: W x H, two boards side by side (each W/2 wide, grain running up the texture,
    // H = 4 board widths long), flat-sawn: growth rings cut at a shallow angle make long arches
    // ("cathedrals") with wavy late-wood lines, fibres, pores, a knot here and there, worn edges.
    // The top 1/32 of the texture is a dark band (the inside of the crate, seen in the gaps).
    // wood: base colour (0..1). alb/nrmOut: level 0 at the start.
    public static void CratePlanks(int W, int H, float wr, float wg, float wb, byte[] alb, byte[] nrmOut)
    {
        float[] hb = new float[W * H];
        int bw = W / 2;
        int dark = Math.Max(4, H / 32);
        Rng r = new Rng(5501u);
        for (int p = 0; p < 2; p++)
        {
            float pith = p == 0 ? 0.42f : 1.25f;       // where the log's heart was, across the board
            float depth = p == 0 ? 0.22f : 0.5f;       // how far the cut is from the heart
            float taper = 0.07f + 0.04f * r.Next();     // the log narrows along the board: arches
            float rings = 7f + 2f * r.Next();
            float kx = 0.35f + 0.3f * r.Next(), ky = 1.2f + 1.6f * r.Next(), kr = 0.06f + 0.03f * r.Next();
            float tone = p == 0 ? 1f : 0.9f;
            for (int y = 0; y < H - dark; y++)
                for (int x = 0; x < bw; x++)
                {
                    float u = (x + 0.5f) / bw, v = (y + 0.5f) / bw;
                    float warp = 0.12f * Fbm(u * 2.5f + p * 13f, v * 0.35f, 3, 81u) + 0.02f * Fbm(u * 9f, v * 2f, 2, 82u);
                    float dx = u - pith + warp;
                    // a knot: the rings bend around it
                    float kdx = (u - kx) / kr, kdy = (v - ky) / (kr * 1.8f);
                    float kd2 = kdx * kdx + kdy * kdy;
                    float knot = (float)Math.Exp(-kd2 * 0.5f);
                    float rad = (float)Math.Sqrt(depth * depth + dx * dx) + taper * v + 0.25f * knot;
                    float ring = rad * rings;
                    float f = ring - (float)Math.Floor(ring);
                    // late wood: a thin darker band at the end of each year
                    float late = Smooth(0.72f, 0.88f, f) * (1f - Smooth(0.94f, 1f, f));
                    float fib = Noise(u * 90f + p * 7f, v * 3f, 83u);
                    float fib2 = Noise(u * 220f, v * 9f, 84u);
                    uint hs = Hash(x / 2, y / 7, 85u + (uint)p) & 0xFFFF;
                    float pore = hs < 900 ? 1f : 0f;
                    float blot = Fbm(u * 1.5f + p * 5f, v * 0.4f, 3, 86u);
                    float edge = Math.Min(u, 1f - u) * bw;       // pixels to the board's edge
                    float wear = edge < 5f ? (1f - edge / 5f) : 0f;
                    float knotDark = Smooth(0.35f, 0f, (float)Math.Sqrt(kd2) * kr / 0.04f);
                    float m = tone * (1f - 0.3f * late) * (1f + 0.05f * fib + 0.03f * fib2) * (1f + 0.1f * blot) * (1f - 0.18f * pore) * (1f - 0.12f * wear) * (1f - 0.45f * knotDark);
                    int o = (y * W + p * bw + x) * 4;
                    alb[o] = (byte)Clamp(wr * m * 255f + 0.5f, 0, 255);
                    alb[o + 1] = (byte)Clamp(wg * m * 255f + 0.5f, 0, 255);
                    alb[o + 2] = (byte)Clamp(wb * m * 255f + 0.5f, 0, 255);
                    alb[o + 3] = 255;
                    // weathered: soft early wood wears away, late wood stands; board edges rounded
                    hb[y * W + p * bw + x] = 0.9f * late + 0.25f * fib + 0.12f * fib2 - 0.6f * pore - 1.6f * wear * wear - 0.5f * knotDark;
                }
        }
        for (int y = H - dark; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                alb[o] = (byte)(wr * 40f); alb[o + 1] = (byte)(wg * 40f); alb[o + 2] = (byte)(wb * 40f); alb[o + 3] = 255;
            }
        HeightToNormal(hb, W, H, 0.35f, false, nrmOut);
    }

    // One steel strap tile: W across the strap, H = 2 W along it (the strap's length runs up the
    // texture and tiles), dark forged iron: brushed along the strap, mottled scale and a touch of
    // rust, bright worn edges, a round rivet head in the middle.
    public static void CrateStrap(int W, int H, float sr, float sg, float sb, byte[] alb, byte[] nrmOut)
    {
        float[] hb = new float[W * H];
        Rng r = new Rng(6607u);
        float[] dent = new float[W * H];
        for (int k = 0; k < 6; k++)
        {
            float cx = r.Next() * W, cy = r.Next() * H, rad = W * (0.06f + 0.08f * r.Next());
            for (int y = (int)(cy - rad) - 1; y <= (int)(cy + rad) + 1; y++)
                for (int x = (int)(cx - rad) - 1; x <= (int)(cx + rad) + 1; x++)
                {
                    if (x < 0 || x >= W) continue;
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy, q = (dx * dx + dy * dy) / (rad * rad);
                    if (q < 1f) { int i = ((y % H + H) % H) * W + x; dent[i] = Math.Max(dent[i], (1f - q) * (1f - q)); }
                }
        }
        float rcx = W * 0.5f, rcy = H * 0.5f, rr = W * 0.2f;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W;
                float a = (y + 0.5f) / H * 6.2832f;
                // periodic along the strap: noise sampled around a circle
                float brushed = Noise(u * 70f + (float)Math.Cos(a) * 1.5f, (float)Math.Sin(a) * 1.5f + 3f, 91u);
                float scale = Fbm(u * 2.2f + (float)Math.Cos(a) * 1.1f + 5f, (float)Math.Sin(a) * 1.1f + 9f, 4, 92u);
                float rust = Smooth(0.25f, 0.6f, Fbm(u * 3f + (float)Math.Cos(a) * 1.6f + 21f, (float)Math.Sin(a) * 1.6f + 2f, 3, 93u));
                float edge = Math.Min(u, 1f - u) * W;
                float worn = edge < 4f ? 1f - edge / 4f : 0f;
                float dx = x + 0.5f - rcx, dy = y + 0.5f - rcy, d = (float)Math.Sqrt(dx * dx + dy * dy) / rr;
                float dome = d < 1f ? (float)Math.Sqrt(1f - d * d) : 0f;
                float ringShadow = d >= 0.9f && d < 1.3f ? 1f - Math.Abs(d - 1.05f) / 0.25f : 0f;
                float lit = d < 1f ? Math.Max(0f, 1f - ((dx / rr + 0.35f) * (dx / rr + 0.35f) + (dy / rr - 0.35f) * (dy / rr - 0.35f)) * 2.5f) : 0f;
                int i = y * W + x;
                float m = (0.82f + 0.08f * brushed + 0.14f * scale) * (1f + 0.35f * worn) * (1f - 0.35f * Math.Max(0f, ringShadow)) * (1f + 0.1f * dome + 0.35f * lit) * (1f - 0.12f * dent[i]);
                float rr2 = 1f + 0.18f * rust, rg2 = 1f - 0.02f * rust, rb2 = 1f - 0.2f * rust;
                int o = i * 4;
                alb[o] = (byte)Clamp(sr * m * rr2 * 255f + 0.5f, 0, 255);
                alb[o + 1] = (byte)Clamp(sg * m * rg2 * 255f + 0.5f, 0, 255);
                alb[o + 2] = (byte)Clamp(sb * m * rb2 * 255f + 0.5f, 0, 255);
                alb[o + 3] = (byte)Clamp((0.55f + 0.35f * worn + 0.2f * dome - 0.35f * rust) * 255f, 0, 255);   // shine (specular shaders)
                hb[i] = 0.25f * brushed + 0.5f * scale + 5f * dome - 1.5f * worn * worn - 1.2f * dent[i];
            }
        HeightToNormal(hb, W, H, 0.5f, true, nrmOut);
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static void HeightToNormal(float[] h, int W, int H, float k, bool wrapY, byte[] nrmOut)
    {
        for (int y = 0; y < H; y++)
        {
            int ym = y > 0 ? y - 1 : (wrapY ? H - 1 : y), yp = y < H - 1 ? y + 1 : (wrapY ? 0 : y);
            for (int x = 0; x < W; x++)
            {
                int xm = x > 0 ? x - 1 : x, xp = x < W - 1 ? x + 1 : x;
                float nx = -(h[y * W + xp] - h[y * W + xm]) * k, ny = -(h[yp * W + x] - h[ym * W + x]) * k;
                float len = (float)Math.Sqrt(nx * nx + ny * ny + 1f);
                int o = (y * W + x) * 4;
                byte bx = (byte)Clamp((nx / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o] = bx; nrmOut[o + 1] = (byte)Clamp((ny / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o + 2] = (byte)Clamp((1f / len * 0.5f + 0.5f) * 255f + 0.5f, 0, 255); nrmOut[o + 3] = bx;
            }
        }
    }

    // pixels with alpha under 128 take the average colour of their opaque neighbours (a few passes),
    // so filtering at a figure's edge never mixes in the colour of the see-through background
    public static void Dilate(byte[] px, int w, int h, int passes)
    {
        bool[] ok = new bool[w * h];
        for (int i = 0; i < w * h; i++) ok[i] = px[i * 4 + 3] >= 128;
        for (int p = 0; p < passes; p++)
        {
            bool[] next = (bool[])ok.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (ok[i]) continue;
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = ny * w + nx;
                        if (!ok[ni]) continue;
                        r += px[ni * 4]; g += px[ni * 4 + 1]; b += px[ni * 4 + 2]; n++;
                    }
                    if (n == 0) continue;
                    px[i * 4] = (byte)(r / n); px[i * 4 + 1] = (byte)(g / n); px[i * 4 + 2] = (byte)(b / n);
                    next[i] = true;
                }
            ok = next;
        }
    }

    public static int MipBytes(int w, int h, int levels)
    {
        int n = 0;
        for (int l = 0; l < levels; l++) { n += w * h * 4; w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); }
        return n;
    }

    // fills the lower mip levels of a mip chain whose level 0 is already at the start of buf
    public static void Mips(byte[] buf, int w, int h, int levels, bool normal)
    {
        int src = 0;
        for (int l = 1; l < levels; l++)
        {
            int nw = Math.Max(1, w >> 1), nh = Math.Max(1, h >> 1);
            int dst = src + w * h * 4;
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1), y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
                    int a = src + (y0 * w + x0) * 4, b = src + (y0 * w + x1) * 4, c = src + (y1 * w + x0) * 4, d = src + (y1 * w + x1) * 4;
                    int o = dst + (y * nw + x) * 4;
                    if (!normal)
                    {
                        for (int ch = 0; ch < 4; ch++) buf[o + ch] = (byte)((buf[a + ch] + buf[b + ch] + buf[c + ch] + buf[d + ch] + 2) >> 2);
                    }
                    else
                    {
                        float nx = (buf[a] + buf[b] + buf[c] + buf[d]) / 510f - 1f;
                        float ny = (buf[a + 1] + buf[b + 1] + buf[c + 1] + buf[d + 1]) / 510f - 1f;
                        float nz = (buf[a + 2] + buf[b + 2] + buf[c + 2] + buf[d + 2]) / 510f - 1f;
                        float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz); if (len < 1e-4f) { nx = 0; ny = 0; nz = 1; len = 1; }
                        nx /= len; ny /= len; nz /= len;
                        byte bx = (byte)Clamp((nx * 0.5f + 0.5f) * 255f + 0.5f, 0, 255), by = (byte)Clamp((ny * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                        buf[o] = bx; buf[o + 1] = by; buf[o + 2] = (byte)Clamp((nz * 0.5f + 0.5f) * 255f + 0.5f, 0, 255); buf[o + 3] = bx;
                    }
                }
            src = dst; w = nw; h = nh;
        }
    }

    // One block texture. e0x/e0y: output origin in source texels; W x H output pixels; T output
    // pixels per texel. nrm/par may be null. hgt may be null (no parallax map wanted).
    // alb/nrmOut/hgt: level 0 written at the start (room for mips after it is the caller's).
    public static string Compose(byte[] src, int sw, int sh, float e0x, float e0y, int W, int H, int T,
        byte[] nrm, int nw, int nh, bool nrmAG, byte[] par, int pw, int ph,
        Layers L, float strength, byte[] alb, byte[] nrmOut, byte[] hgt, float[] hbuf)
    {
        return Compose(src, sw, sh, e0x, e0y, W, H, T, nrm, nw, nh, nrmAG, par, pw, ph, L, strength, 1f, alb, nrmOut, hgt, hbuf);
    }

    // warpMul: how far the game's pixel edges wander (1 for stone; less for a figure whose features
    // are one or two pixels wide)
    public static string Compose(byte[] src, int sw, int sh, float e0x, float e0y, int W, int H, int T,
        byte[] nrm, int nw, int nh, bool nrmAG, byte[] par, int pw, int ph,
        Layers L, float strength, float warpMul, byte[] alb, byte[] nrmOut, byte[] hgt, float[] hbuf)
    {
        float s = strength;
        float kSharp = T / 5f, kSoft = T / 10f;
        float warpScale = 0.6f / 127f;
        double sumL = 0;
        for (int y = 0; y < H; y++)
        {
            int ly = (y % L.H) * L.W;
            for (int x = 0; x < W; x++)
            {
                int li = ly + (x % L.W);
                float sx = e0x + (x + 0.5f) / T, sy = e0y + (y + 0.5f) / T;
                float wx = L.warpX[li] * warpScale * s * warpMul, wy = L.warpY[li] * warpScale * s * warpMul;
                float r, g, b, a, r2, g2, b2, a2;
                Sharp(src, sw, sh, sx + wx, sy + wy, kSharp, out r, out g, out b, out a);
                Sharp(src, sw, sh, sx + wx, sy + wy, kSoft, out r2, out g2, out b2, out a2);
                float m = 1f + s * ((L.mul[li] / 255f + 0.4f) - 1f);
                float t = L.tint[li] / 127f * 0.035f * s;
                int o = (y * W + x) * 4;
                alb[o] = (byte)Clamp(r * m * (1f + t), 0f, 255f);
                alb[o + 1] = (byte)Clamp(g * m, 0f, 255f);
                alb[o + 2] = (byte)Clamp(b * m * (1f - t), 0f, 255f);
                alb[o + 3] = (byte)Clamp(a, 0f, 255f);
                float lum = (0.3f * r2 + 0.55f * g2 + 0.15f * b2) / 255f;
                sumL += lum;
                // relief: lighter patches of the game's pattern stand a little proud, plus the detail
                hbuf[y * W + x] = s * (L.height[li] / 256f + lum * 5f * (L.T / 32f));
            }
        }
        // normals from the height (px units): the game's own normal map (bevels) is kept and the
        // relief is laid over it
        float kN = 0.5f;
        float hMin = float.MaxValue, hMax = float.MinValue;
        for (int i = 0; i < W * H; i++) { float v = hbuf[i]; if (v < hMin) hMin = v; if (v > hMax) hMax = v; }
        for (int y = 0; y < H; y++)
        {
            int ym = y > 0 ? y - 1 : y, yp = y < H - 1 ? y + 1 : y;
            for (int x = 0; x < W; x++)
            {
                int xm = x > 0 ? x - 1 : x, xp = x < W - 1 ? x + 1 : x;
                float dx = (hbuf[y * W + xp] - hbuf[y * W + xm]) * kN;
                float dy = (hbuf[yp * W + x] - hbuf[ym * W + x]) * kN;
                float nx = -dx, ny = -dy, nz = 1f;
                float sx = e0x + (x + 0.5f) / T, sy = e0y + (y + 0.5f) / T;
                if (nrm != null)
                {
                    float qx = sx / sw * nw, qy = sy / sh * nh;
                    float ox = Bilin(nrm, nw, nh, qx, qy, nrmAG ? 3 : 0) / 127.5f - 1f;
                    float oy = Bilin(nrm, nw, nh, qx, qy, 1) / 127.5f - 1f;
                    float oz = (float)Math.Sqrt(Math.Max(0f, 1f - ox * ox - oy * oy));
                    // whiteout blend
                    nx = ox + nx; ny = oy + ny; nz = oz * nz;
                }
                float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= len; ny /= len; nz /= len;
                int o = (y * W + x) * 4;
                byte bx = (byte)Clamp((nx * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o] = bx; nrmOut[o + 1] = (byte)Clamp((ny * 0.5f + 0.5f) * 255f + 0.5f, 0, 255);
                nrmOut[o + 2] = (byte)Clamp((nz * 0.5f + 0.5f) * 255f + 0.5f, 0, 255); nrmOut[o + 3] = bx;
                if (hgt != null)
                {
                    float ph0 = par != null ? Bilin(par, pw, ph, sx / sw * pw, sy / sh * ph, 1) / 255f : 0.5f;
                    float hd = hMax > hMin ? (hbuf[y * W + x] - hMin) / (hMax - hMin) - 0.5f : 0f;
                    byte hv = (byte)Clamp((ph0 + 0.35f * hd) * 255f, 0, 255);
                    hgt[o] = hv; hgt[o + 1] = hv; hgt[o + 2] = hv; hgt[o + 3] = 255;
                }
            }
        }
        return "relief " + hMin.ToString("0.0") + ".." + hMax.ToString("0.0") + " px";
    }
}

// ---------------------------------------------------------------------------------------------
// HD stone overlay (first person only). The game's bricks all use one 64x64 pixel-art texture per
// material, laid out as an unfolded box: each face of a brick shows only 8 x 8 of those pixels.
// First person draws its own copies of the bricks (FPBricks); their texture coordinates are
// re-mapped onto just the part of the texture the brick mesh uses, and their material is a copy
// whose textures are rebuilt at StoneHDScale output pixels per game pixel (32: 256 x 256 per face):
//   colour   each game pixel keeps its colour, so the palette and pattern are the game's; the
//            square pixel edges become irregular stone edges, with grain, sand, mottling, pores
//            and hairline cracks multiplied in (StoneCore)
//   normal   the game's own normal map (bevels) plus the relief of the new detail
//   height   the game's parallax map plus the relief (half resolution), where parallax is used
// The work runs on a background thread (about half a second per texture) while the copy shows the
// game's own texture through re-mapped tiling, so nothing waits for it; the finished textures go
// to the graphics card one per frame. Textures for a new floor start building as soon as the game
// changes the floor's textures. Work buffers are reused between textures and let go when idle.
// A texture is at most StoneHDMaxSize on its longest side (meshes that use the whole texture get
// fewer pixels per game pixel). Textures named Block* (the game's wall, floor and switch stone)
// are replaced; F1 flips HD / game textures in first person.
// The tabletop view never sees any of this: it draws the game's own bricks.
// ---------------------------------------------------------------------------------------------
public static class StoneHD
{
    // one set of HD textures: per game texture set (colour, normal map, parallax map, tiling) and
    // brick mesh area. Kept for the session; its textures are freed when it is least recently used
    // beyond StoneHDKeep ("cold") and built again when a brick needs it.
    public sealed class Tex
    {
        public string key, name;
        public Texture srcMain, srcBump, srcPar;
        public bool hasBump, wantPar;
        public Vector2 phScale, phOffset;
        public Texture2D tAlb, tNrm, tHgt;
        public bool ready, failed, queued;
        public int gen, order;
        public float freedAt = -100f;
        public bool sprite;                 // a flat figure made 3D (statues): opaque, gentler detail
        public int style;                   // 0 stone, 1 wood (crates)
        public bool keepST;                 // whole texture, drawn with the game's own tiling (props)
        public float e0x, e0y;
        public int W, H, sw, sh, T;
        public readonly List<Mat> mats = new List<Mat>();
    }
    // the first-person copy of one game material (plus its shine copies) drawing a Tex
    public sealed class Mat
    {
        public Material orig, hd;
        public Tex tex;
        public Vector4 crop;
        public readonly List<Material> followers = new List<Material>();
    }

    private sealed class Job
    {
        public Tex e;
        public int gen;
        public byte[] src, nrm, par;
        public int sw, sh, nw, nh, pw, ph;
        public bool nrmAG;
        public byte[] alb, nOut, hHalf;
        public int levels, hw, hh, hLevels;
        public volatile bool done;
        public string error, note;
        public long ms;
        public int uploadStep;
        public long uploadMs;
    }

    private static readonly Dictionary<string, Tex> texes = new Dictionary<string, Tex>();
    private static readonly Dictionary<string, Mat> mats = new Dictionary<string, Mat>();
    private static readonly Dictionary<int, Mat> byHd = new Dictionary<int, Mat>();
    private static readonly List<Job> queue = new List<Job>();
    private static Job running;
    private static StoneCore.Layers layers;
    private static readonly object layersLock = new object();
    private static System.Threading.Thread layerThread;
    private static string layerNote = "";
    private static bool show = true;
    private static int orderCounter, dumped, logs, stampSeq = 1;
    private static readonly Dictionary<int, Vector4> crops = new Dictionary<int, Vector4>();
    private static readonly Dictionary<string, int> cropUse = new Dictionary<string, int>();
    private static readonly Dictionary<string, Vector4> cropByKey = new Dictionary<string, Vector4>();
    private static float nextPoll, lastWork;
    private static LevelBlockMod lbm;
    private static readonly Dictionary<string, bool> dumpedSrc = new Dictionary<string, bool>();
    private static readonly Dictionary<string, byte[]> pool = new Dictionary<string, byte[]>();
    private static readonly Dictionary<int, float[]> fpool = new Dictionary<int, float[]>();

    public static bool Active { get { return FPConfig.StoneHD; } }
    // changes whenever anything shown changes (HD ready, freed, F1): moving bricks re-apply then
    public static int Stamp { get { return stampSeq; } }
    private static int T { get { return Mathf.Clamp(FPConfig.StoneHDScale, 8, 48); } }

    public static string Install()
    {
        if (!FPConfig.StoneHD) return "off (StoneHD=false)";
        try
        {
            int t = T;
            layerThread = new System.Threading.Thread(delegate () { EnsureLayers(t); });
            layerThread.IsBackground = true;
            layerThread.Priority = System.Threading.ThreadPriority.BelowNormal;
            layerThread.Start();
            return "on: " + t + " pixels per game pixel (at most " + FPConfig.StoneHDMaxSize + " per texture), detail " + FPConfig.StoneHDStrength.ToString("0.##")
                + " (detail being prepared in the background; F1 = HD / game textures)";
        }
        catch (Exception e) { return "layer thread failed: " + e.Message; }
    }

    private static void EnsureLayers(int t)
    {
        lock (layersLock)
        {
            if (layers != null && layers.T == t) return;
            System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
            layers = StoneCore.MakeLayers(36 * t, 26 * t, t, 20260926u);
            layerNote = "detail prepared in " + w.ElapsedMilliseconds + " ms (" + layers.W + "x" + layers.H + ")";
        }
    }

    private static byte[] Buf(string what, int len)
    {
        string k = what + len;
        byte[] b;
        if (!pool.TryGetValue(k, out b)) { b = new byte[len]; pool[k] = b; }
        return b;
    }

    // the part of the texture a brick mesh uses, in its raw texture coordinates: x, y, w, h
    public static Vector4 Crop(Mesh m)
    {
        Vector4 c;
        int id = m.GetInstanceID();
        if (!crops.TryGetValue(id, out c))
        {
            Vector2[] uv = m.uv;
            if (uv == null || uv.Length == 0) c = new Vector4(0, 0, 1, 1);
            else
            {
                Vector2 lo = uv[0], hi = uv[0];
                for (int i = 1; i < uv.Length; i++) { lo = Vector2.Min(lo, uv[i]); hi = Vector2.Max(hi, uv[i]); }
                float pad = 0.25f / 64f;
                lo -= new Vector2(pad, pad); hi += new Vector2(pad, pad);
                c = new Vector4(lo.x, lo.y, Mathf.Max(hi.x - lo.x, 1e-3f), Mathf.Max(hi.y - lo.y, 1e-3f));
            }
            crops[id] = c;
            if (logs++ < 10)
                Debug.Log("[FirstPersonLoD] VR stone HD: brick mesh " + m.name + " uses texture area u " + c.x.ToString("0.000") + ".." + (c.x + c.z).ToString("0.000")
                    + ", v " + c.y.ToString("0.000") + ".." + (c.y + c.w).ToString("0.000") + " (" + (c.z * 100f).ToString("0") + "% x " + (c.w * 100f).ToString("0") + "% of the texture)");
        }
        return c;
    }

    // counts which brick meshes are common, so a new floor's textures are prepared for those only
    public static void CountUse(Vector4 c)
    {
        string k = c.ToString("F4");
        int n; cropUse.TryGetValue(k, out n); cropUse[k] = n + 1;
        cropByKey[k] = c;
    }

    public static bool Eligible(Material m)
    {
        if (!FPConfig.StoneHD || m == null) return false;
        if (byHd.ContainsKey(m.GetInstanceID()) || m.name.IndexOf("_hd") >= 0) return false;
        Texture t = m.mainTexture;
        return t != null && StoneName(t.name) && !t.name.EndsWith("_hd");
    }

    // texture names that get the stone treatment on bricks (StoneHDTextures: name prefixes)
    private static string[] stoneNames; private static string stoneSrc;
    private static bool StoneName(string n)
    {
        if (stoneNames == null || stoneSrc != FPConfig.StoneHDTextures)
        {
            stoneSrc = FPConfig.StoneHDTextures;
            List<string> o = new List<string>();
            foreach (string p in stoneSrc.Split(',')) { string k = p.Trim(); if (k.Length > 0) o.Add(k); }
            stoneNames = o.ToArray();
        }
        for (int i = 0; i < stoneNames.Length; i++) if (n.StartsWith(stoneNames[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool Legacy(Material m) { return m.shader != null && m.shader.name.StartsWith("Legacy"); }

    public static bool IsOurs(Material m) { return m != null && byHd.ContainsKey(m.GetInstanceID()); }

    private static Tex GetTex(Material m, Vector4 crop)
    {
        Texture main = m.mainTexture;
        // Standard uses a normal map only with its keyword; the legacy bumped shaders always do
        bool hasBump = m.HasProperty("_BumpMap") && (Legacy(m) || m.IsKeywordEnabled("_NORMALMAP")) && m.GetTexture("_BumpMap") != null;
        bool wantPar = m.HasProperty("_ParallaxMap") && m.IsKeywordEnabled("_PARALLAXMAP");
        Texture bump = hasBump ? m.GetTexture("_BumpMap") : null;
        Texture par = wantPar ? m.GetTexture("_ParallaxMap") : null;
        Vector2 S = m.mainTextureScale, O = m.mainTextureOffset;
        string key = main.GetInstanceID() + "/" + (bump != null ? bump.GetInstanceID() : 0) + "/" + (wantPar ? (par != null ? par.GetInstanceID() : -1) : 0)
            + "/" + crop.ToString("F4") + "/" + S.ToString("F4") + O.ToString("F4");
        Tex e;
        if (texes.TryGetValue(key, out e)) { Touch(e); return e; }
        e = new Tex();
        e.key = key; e.srcMain = main; e.srcBump = bump; e.srcPar = par; e.hasBump = hasBump; e.wantPar = wantPar;
        e.name = main.name + (hasBump ? "" : " (no normal map)") + (wantPar ? " +parallax" : "");
        // until the HD textures are ready the copy shows the game's texture through re-mapped tiling
        e.phScale = new Vector2(crop.z * S.x, crop.w * S.y);
        e.phOffset = new Vector2(crop.x * S.x + O.x, crop.y * S.y + O.y);
        e.sw = main.width; e.sh = main.height;
        e.e0x = e.phOffset.x * e.sw; e.e0y = e.phOffset.y * e.sh;
        // the same HD size whatever the game texture's size (some floors use 16 x 16 ones), and
        // no bigger than StoneHDMaxSize (a mesh using the whole texture gets fewer pixels)
        float texels = Mathf.Max(e.phScale.x * e.sw, e.phScale.y * e.sh);
        float tWant = T * 64f / Mathf.Max(8, e.sw);
        float tCap = Mathf.Max(256, FPConfig.StoneHDMaxSize) / Mathf.Max(1f, texels);
        e.T = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(tWant, tCap)), 4, 256);
        e.W = Mathf.Clamp(Mathf.RoundToInt(e.phScale.x * e.sw * e.T), 4, 2048);
        e.H = Mathf.Clamp(Mathf.RoundToInt(e.phScale.y * e.sh * e.T), 4, 2048);
        texes[key] = e;
        Touch(e);
        return e;
    }

    // most recently used; a texture set with nothing built (new, or freed) is queued
    private static void Touch(Tex e)
    {
        e.order = ++orderCounter;
        // set aside to stay within StoneHDKeep: not built again straight away (no build/free cycle)
        if (Time.realtimeSinceStartup - e.freedAt < 20f) return;
        if (!e.ready && !e.queued && !e.failed) { e.queued = true; Job j = new Job(); j.e = e; j.gen = e.gen; queue.Add(j); }
    }

    // the HD copy of a game brick material, for meshes whose texture coordinates were re-mapped to crop
    public static Material For(Material m, Vector4 crop)
    {
        if (!Eligible(m)) return null;
        string key = m.GetInstanceID() + "/" + m.mainTexture.GetInstanceID() + "/" + crop.ToString("F4");
        Mat me;
        if (mats.TryGetValue(key, out me)) { Touch(me.tex); return me.hd; }
        try
        {
            Tex t = GetTex(m, crop);
            me = new Mat();
            me.orig = m; me.tex = t; me.crop = crop;
            me.hd = new Material(m);
            me.hd.name = m.name + "_hd";
            ShowMat(me);
            mats[key] = me;
            t.mats.Add(me);
            byHd[me.hd.GetInstanceID()] = me;
            return me.hd;
        }
        catch (Exception ex)
        {
            Debug.Log("[FirstPersonLoD] VR stone HD: " + m.name + " failed (" + ex.GetType().Name + ": " + ex.Message + "); the game's texture stays");
            return null;
        }
    }

    // a figure made 3D (Statues): its texture frame rebuilt like the stone, gentler, at most 512 px
    // a figure made 3D (Statues): the whole texture (a single picture or an animation sheet) rebuilt
    // like the stone but gentler and without wandering edges; at most 1024 px. The 3D meshes carry
    // texture-space coordinates, so it is laid on with no tiling.
    public static Tex TexForSprite(Material m)
    {
        if (!FPConfig.StoneHD || m == null || m.mainTexture == null) return null;
        try
        {
            Texture main = m.mainTexture;
            Texture bump = m.HasProperty("_BumpMap") ? m.GetTexture("_BumpMap") : null;
            string key = "spr/" + main.GetInstanceID() + "/" + (bump != null ? bump.GetInstanceID() : 0);
            Tex e;
            if (texes.TryGetValue(key, out e)) { Touch(e); return e; }
            e = new Tex();
            e.key = key; e.srcMain = main; e.srcBump = bump; e.hasBump = bump != null; e.wantPar = false; e.sprite = true;
            e.name = main.name + " (3D figure)";
            e.phScale = Vector2.one; e.phOffset = Vector2.zero;
            e.sw = main.width; e.sh = main.height;
            e.e0x = 0f; e.e0y = 0f;
            float texels = Mathf.Max(e.sw, e.sh);
            e.T = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(16f, 1024f / Mathf.Max(1f, texels))), 1, 16);
            e.W = Mathf.Clamp(e.sw * e.T, 4, 2048);
            e.H = Mathf.Clamp(e.sh * e.T, 4, 2048);
            texes[key] = e;
            Touch(e);
            return e;
        }
        catch (Exception) { return null; }
    }

    // a prop drawn with its own texture and tiling (crates: style 1, wood): the whole texture rebuilt
    // at up to 512 px and laid on with a property block, tiling unchanged
    public static Tex TexForProp(Material m, int style)
    {
        if (!FPConfig.StoneHD || m == null || m.mainTexture == null) return null;
        try
        {
            Texture main = m.mainTexture;
            Texture bump = m.HasProperty("_BumpMap") ? m.GetTexture("_BumpMap") : null;
            Vector2 S = m.mainTextureScale, O = m.mainTextureOffset;
            string key = "prop" + style + "/" + main.GetInstanceID() + "/" + (bump != null ? bump.GetInstanceID() : 0) + "/" + S.ToString("F4") + O.ToString("F4");
            Tex e;
            if (texes.TryGetValue(key, out e)) { Touch(e); return e; }
            e = new Tex();
            e.key = key; e.srcMain = main; e.srcBump = bump; e.hasBump = bump != null; e.wantPar = false; e.style = style; e.keepST = true;
            e.name = main.name + (style == 1 ? " (wood)" : " (prop)");
            e.phScale = S; e.phOffset = O;
            e.sw = main.width; e.sh = main.height;
            e.e0x = 0f; e.e0y = 0f;
            e.T = Mathf.Clamp(Mathf.FloorToInt(512f / Mathf.Max(1, Mathf.Max(e.sw, e.sh))), 1, 64);
            e.W = Mathf.Clamp(e.sw * e.T, 4, 1024);
            e.H = Mathf.Clamp(e.sh * e.T, 4, 1024);
            texes[key] = e;
            Touch(e);
            return e;
        }
        catch (Exception) { return null; }
    }

    // for bricks the game itself changes (moving parts, switches): the texture set to put on them
    // with a property block, leaving the game's material alone
    public static Tex TexFor(Material m, Vector4 crop)
    {
        if (!Eligible(m)) return null;
        try { return GetTex(m, crop); } catch (Exception) { return null; }
    }

    private static readonly MaterialPropertyBlock empty = new MaterialPropertyBlock();
    public static void Fill(MaterialPropertyBlock b, Tex e)
    {
        b.Clear();
        if (show && e.ready && e.tAlb != null)
        {
            b.SetTexture("_MainTex", e.tAlb);
            if (e.hasBump && e.tNrm != null) b.SetTexture("_BumpMap", e.tNrm);
            if (e.wantPar && e.tHgt != null) b.SetTexture("_ParallaxMap", e.tHgt);
            Vector4 hdST = e.keepST ? new Vector4(e.phScale.x, e.phScale.y, e.phOffset.x, e.phOffset.y) : new Vector4(1f, 1f, 0f, 0f);
            b.SetVector("_MainTex_ST", hdST);
            // legacy shaders tile the normal map on its own
            b.SetVector("_BumpMap_ST", e.hasBump && e.tNrm != null ? hdST : new Vector4(e.phScale.x, e.phScale.y, e.phOffset.x, e.phOffset.y));
        }
        else
        {
            b.SetTexture("_MainTex", e.srcMain);
            b.SetVector("_MainTex_ST", new Vector4(e.phScale.x, e.phScale.y, e.phOffset.x, e.phOffset.y));
            b.SetVector("_BumpMap_ST", new Vector4(e.phScale.x, e.phScale.y, e.phOffset.x, e.phOffset.y));
        }
    }
    public static MaterialPropertyBlock Empty { get { return empty; } }

    // shine copies of an HD material follow it when its textures change
    public static void Follow(Material copy, Material hdParent)
    {
        if (copy == null || hdParent == null) return;
        Mat me;
        if (!byHd.TryGetValue(hdParent.GetInstanceID(), out me)) return;
        me.followers.Add(copy);
        byHd[copy.GetInstanceID()] = me;
        Apply(copy, me.tex);
    }

    public static void Unfollow(Material copy)
    {
        if (copy == null) return;
        Mat me;
        if (!byHd.TryGetValue(copy.GetInstanceID(), out me) || me.hd == copy) return;
        me.followers.Remove(copy);
        byHd.Remove(copy.GetInstanceID());
    }

    private static void Apply(Material mat, Tex e)
    {
        if (mat == null) return;
        if (show && e.ready && e.tAlb != null)
        {
            mat.mainTexture = e.tAlb;
            if (e.hasBump && e.tNrm != null) mat.SetTexture("_BumpMap", e.tNrm);
            if (e.wantPar && e.tHgt != null) mat.SetTexture("_ParallaxMap", e.tHgt);
            mat.mainTextureScale = Vector2.one;
            mat.mainTextureOffset = Vector2.zero;
            // legacy shaders tile the normal map on its own
            if (mat.HasProperty("_BumpMap")) { mat.SetTextureScale("_BumpMap", e.hasBump && e.tNrm != null ? Vector2.one : e.phScale); mat.SetTextureOffset("_BumpMap", e.hasBump && e.tNrm != null ? Vector2.zero : e.phOffset); }
        }
        else
        {
            mat.mainTexture = e.srcMain;
            if (e.hasBump) mat.SetTexture("_BumpMap", e.srcBump);
            if (e.wantPar) mat.SetTexture("_ParallaxMap", e.srcPar);
            mat.mainTextureScale = e.phScale;
            mat.mainTextureOffset = e.phOffset;
            if (mat.HasProperty("_BumpMap")) { mat.SetTextureScale("_BumpMap", e.phScale); mat.SetTextureOffset("_BumpMap", e.phOffset); }
        }
    }

    private static void ShowMat(Mat me)
    {
        Apply(me.hd, me.tex);
        for (int i = 0; i < me.followers.Count; i++) Apply(me.followers[i], me.tex);
    }

    private static void ShowTex(Tex e)
    {
        for (int i = 0; i < e.mats.Count; i++) ShowMat(e.mats[i]);
        stampSeq++;
    }

    private static void Free(Tex e)
    {
        e.ready = false;
        ShowTex(e);
        if (e.tAlb != null) UnityEngine.Object.Destroy(e.tAlb);
        if (e.tNrm != null) UnityEngine.Object.Destroy(e.tNrm);
        if (e.tHgt != null) UnityEngine.Object.Destroy(e.tHgt);
        e.tAlb = e.tNrm = e.tHgt = null;
    }

    // at most StoneHDKeep texture sets hold textures; the least recently used give theirs back
    // (their bricks show the game's texture until a brick asks for them again)
    private static void Evict()
    {
        int keep = Mathf.Max(4, FPConfig.StoneHDKeep);
        while (true)
        {
            int warm = 0;
            Tex oldest = null;
            foreach (Tex x in texes.Values)
            {
                if (!x.ready) continue;
                warm++;
                if (oldest == null || x.order < oldest.order) oldest = x;
            }
            if (warm <= keep || oldest == null) break;
            Free(oldest);
            oldest.freedAt = Time.realtimeSinceStartup;
        }
    }

    // a different detail strength: every texture set is built again (in the background)
    public static void Rebuild()
    {
        foreach (Tex e in texes.Values)
        {
            e.gen++;
            e.failed = false;
            Free(e);
            e.queued = false;
            for (int i = queue.Count - 1; i >= 0; i--) if (queue[i].e == e) queue.RemoveAt(i);
            Touch(e);
        }
    }

    // RGBA bytes of any texture, rows bottom-up; unreadable textures go through a render texture
    public static byte[] ReadPixels(Texture t, out int w, out int h) { return Pixels(t, false, out w, out h); }
    private static byte[] Pixels(Texture t, bool linear, out int w, out int h)
    {
        w = t.width; h = t.height;
        Texture2D t2 = t as Texture2D;
        if (t2 != null)
        {
            try
            {
                Color32[] c = t2.GetPixels32();
                byte[] b = new byte[c.Length * 4];
                for (int i = 0; i < c.Length; i++) { b[i * 4] = c[i].r; b[i * 4 + 1] = c[i].g; b[i * 4 + 2] = c[i].b; b[i * 4 + 3] = c[i].a; }
                return b;
            }
            catch (Exception) { }   // not readable: copy it through the graphics card
        }
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default);
        RenderTexture prev = RenderTexture.active;
        Graphics.Blit(t, rt);
        RenderTexture.active = rt;
        Texture2D tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
        tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tmp.Apply(false, false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        Color32[] cc = tmp.GetPixels32();
        UnityEngine.Object.Destroy(tmp);
        byte[] bb = new byte[cc.Length * 4];
        for (int i = 0; i < cc.Length; i++) { bb[i * 4] = cc[i].r; bb[i * 4 + 1] = cc[i].g; bb[i * 4 + 2] = cc[i].b; bb[i * 4 + 3] = cc[i].a; }
        return bb;
    }

    private static int Levels(int w, int h)
    {
        int n = 1;
        while (w > 1 || h > 1) { w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); n++; }
        return n;
    }

    private static void Start(Job j)
    {
        Tex e = j.e;
        j.src = Pixels(e.srcMain, false, out j.sw, out j.sh);
        if (e.srcBump != null)
        {
            j.nrm = Pixels(e.srcBump, true, out j.nw, out j.nh);
            // a normal map is stored with x in alpha (and y in green) on PC; a plain one has x in red
            int aMin = 255, aMax = 0;
            for (int i = 3; i < j.nrm.Length; i += 4) { int a = j.nrm[i]; if (a < aMin) aMin = a; if (a > aMax) aMax = a; }
            j.nrmAG = aMax - aMin > 24;
        }
        if (e.wantPar && e.srcPar != null) j.par = Pixels(e.srcPar, true, out j.pw, out j.ph);
        Dump(j.src, j.sw, j.sh, "src_" + e.srcMain.name);
        j.levels = Levels(e.W, e.H);
        j.hw = Math.Max(1, e.W >> 1); j.hh = Math.Max(1, e.H >> 1); j.hLevels = Levels(j.hw, j.hh);
        int t = T, te = e.T;
        float strength = FPConfig.StoneHDStrength * (e.sprite ? 0.6f : 1f);
        float warpMul = e.sprite ? 0f : 1f;
        if (e.sprite) StoneCore.Dilate(j.src, j.sw, j.sh, 3);   // see-through pixels take their neighbours' colour, so edges don't darken
        bool opaque = e.sprite;
        int bytes = StoneCore.MipBytes(e.W, e.H, j.levels);
        // buffers are taken here, on the main thread; the worker only fills them, and the next job
        // only starts after this one's last upload
        j.alb = Buf("alb", bytes);
        j.nOut = Buf("nrm", bytes);
        byte[] hFull = e.wantPar ? Buf("hgt", bytes) : null;
        j.hHalf = e.wantPar ? Buf("hgh", StoneCore.MipBytes(j.hw, j.hh, j.hLevels)) : null;
        float[] hb;
        if (!fpool.TryGetValue(e.W * e.H, out hb)) { hb = new float[e.W * e.H]; fpool[e.W * e.H] = hb; }
        running = j;
        System.Threading.Thread th = new System.Threading.Thread(delegate ()
        {
            try
            {
                System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                EnsureLayers(t);
                if (e.style == 1)
                    j.note = StoneCore.ComposeWood(j.src, j.sw, j.sh, e.W, e.H, te, j.nrm, j.nw, j.nh, j.nrmAG, strength, j.alb, j.nOut, hb);
                else
                    j.note = StoneCore.Compose(j.src, j.sw, j.sh, e.e0x, e.e0y, e.W, e.H, te, j.nrm, j.nw, j.nh, j.nrmAG, j.par, j.pw, j.ph,
                        layers, strength, warpMul, j.alb, j.nOut, hFull, hb);
                if (opaque) for (int i = 3; i < e.W * e.H * 4; i += 4) j.alb[i] = 255;   // the 3D shape is the outline
                StoneCore.Mips(j.alb, e.W, e.H, j.levels, false);
                StoneCore.Mips(j.nOut, e.W, e.H, j.levels, true);
                if (hFull != null)
                {
                    // parallax height at half size: the second level of the full chain onwards
                    StoneCore.Mips(hFull, e.W, e.H, j.levels, false);
                    Buffer.BlockCopy(hFull, e.W * e.H * 4, j.hHalf, 0, j.hHalf.Length);
                }
                j.ms = w.ElapsedMilliseconds;
            }
            catch (Exception ex) { j.error = ex.GetType().Name + ": " + ex.Message; }
            j.done = true;
        });
        th.IsBackground = true;
        th.Priority = System.Threading.ThreadPriority.BelowNormal;
        th.Start();
    }

    private static Texture2D Upload(byte[] data, int w, int h, int levels, bool linear, string name) { return Upload(data, w, h, levels, linear, name, TextureWrapMode.Clamp); }
    private static Texture2D Upload(byte[] data, int w, int h, int levels, bool linear, string name, TextureWrapMode wrap)
    {
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, true, linear);
        t.name = name;
        if (t.mipmapCount == levels && data.Length == StoneCore.MipBytes(w, h, levels)) t.LoadRawTextureData(data);
        else
        {
            Color32[] c = new Color32[w * h];
            for (int i = 0; i < c.Length; i++) c[i] = new Color32(data[i * 4], data[i * 4 + 1], data[i * 4 + 2], data[i * 4 + 3]);
            t.SetPixels32(c);
            t.Apply(true, false);
        }
        t.filterMode = FilterMode.Trilinear;
        t.anisoLevel = 8;
        t.wrapMode = wrap;
        t.Apply(false, true);   // to the graphics card; the copy in memory is dropped
        return t;
    }

    // one texture to the graphics card per frame; true when the job is finished
    private static bool UploadStep(Job j)
    {
        Tex e = j.e;
        if (j.gen != e.gen) return true;   // rebuilt at a new strength meanwhile: a newer job is queued
        if (j.error != null)
        {
            e.failed = true; e.queued = false;
            Debug.Log("[FirstPersonLoD] VR stone HD: " + e.name + " failed on the worker (" + j.error + "); the game's texture stays");
            return true;
        }
        System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            switch (j.uploadStep++)
            {
                case 0:
                    if (e.tAlb != null) UnityEngine.Object.Destroy(e.tAlb);
                    e.tAlb = Upload(j.alb, e.W, e.H, j.levels, false, e.srcMain.name + "_hd", (e.sprite || e.keepST) ? e.srcMain.wrapMode : TextureWrapMode.Clamp);
                    if (FPConfig.StoneHDDump && dumped < 2) { dumped++; DumpLevel0(j.alb, e.W, e.H, "hd_" + e.srcMain.name); }
                    break;
                case 1:
                    if (e.hasBump)
                    {
                        if (e.tNrm != null) UnityEngine.Object.Destroy(e.tNrm);
                        e.tNrm = Upload(j.nOut, e.W, e.H, j.levels, true, e.srcMain.name + "_hdnormal", (e.sprite || e.keepST) ? e.srcMain.wrapMode : TextureWrapMode.Clamp);
                    }
                    break;
                case 2:
                    if (j.hHalf != null)
                    {
                        if (e.tHgt != null) UnityEngine.Object.Destroy(e.tHgt);
                        e.tHgt = Upload(j.hHalf, j.hw, j.hh, j.hLevels, true, e.srcMain.name + "_hdheight");
                    }
                    break;
            }
            j.uploadMs += w.ElapsedMilliseconds;
            if (j.uploadStep < 3) return false;
            e.ready = true;
            e.queued = false;
            ShowTex(e);
            int users = 0;
            for (int i = 0; i < e.mats.Count; i++) users += 1 + e.mats[i].followers.Count;
            Debug.Log("[FirstPersonLoD] VR stone HD: " + e.name + " -> " + e.W + "x" + e.H + " (colour" + (e.hasBump ? ", normal" : "") + (e.tHgt != null ? ", height " + j.hw + "x" + j.hh : "") + ")"
                + " built in " + j.ms + " ms on the worker, uploaded in " + j.uploadMs + " ms over 3 frames; game normal map read as " + (j.nrm == null ? "none" : j.nrmAG ? "x in alpha" : "x in red")
                + "; " + j.note + (layerNote.Length > 0 ? "; " + layerNote : "") + "; " + users + " materials" + (show ? "" : " (showing the game's textures: F1)"));
            layerNote = "";
            Evict();
        }
        catch (Exception ex)
        {
            e.failed = true; e.queued = false;
            Debug.Log("[FirstPersonLoD] VR stone HD: " + e.name + " upload failed (" + ex.GetType().Name + ": " + ex.Message + "); the game's texture stays");
            return true;
        }
        return true;
    }

    private static string DumpDir()
    {
        string dir = Path.Combine(Path.Combine(Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "BepInEx"), "FirstPersonLoD_dump"), "stone");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // only with StoneHDDump (a PNG of a big texture takes a few hundred ms)
    private static void Dump(byte[] px, int w, int h, string name)
    {
        if (!FPConfig.StoneHDDump || dumpedSrc.ContainsKey(name)) return;
        dumpedSrc[name] = true;
        try
        {
            Texture2D tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tmp.LoadRawTextureData(px);
            tmp.Apply(false, false);
            File.WriteAllBytes(Path.Combine(DumpDir(), name + ".png"), tmp.EncodeToPNG());
            UnityEngine.Object.Destroy(tmp);
        }
        catch (Exception ex) { Debug.Log("[FirstPersonLoD] VR stone HD: could not save " + name + ": " + ex.Message); }
    }

    private static void DumpLevel0(byte[] px, int w, int h, string name)
    {
        try
        {
            byte[] l0 = new byte[w * h * 4];
            Buffer.BlockCopy(px, 0, l0, 0, l0.Length);
            Dump(l0, w, h, name);
        }
        catch (Exception) { }
    }

    // every frame (first person and the tabletop view)
    public static void Tick(bool firstPerson)
    {
        if (!FPConfig.StoneHD) return;
        if (firstPerson && Input.GetKeyDown(KeyCode.F1))
        {
            show = !show;
            foreach (Tex e in texes.Values) ShowTex(e);
            Debug.Log("[FirstPersonLoD] VR stone HD: showing " + (show ? "the HD stone" : "the game's own textures") + " (F1)");
        }
        float now = Time.realtimeSinceStartup;
        if (running != null)
        {
            lastWork = now;
            if (!running.done) return;
            if (!UploadStep(running)) return;
            running.src = running.nrm = running.par = null;
            running = null;
            return;   // the next texture starts next frame
        }
        // a new floor: the game has put new textures on its brick materials; start on them now,
        // for the brick meshes that are common (not one-off pieces)
        if (now >= nextPoll)
        {
            nextPoll = now + 1f;
            // first-person copies made before the game changed a material's texture (a room prepared
            // just before a floor change) move to the new texture
            foreach (Mat me in mats.Values)
            {
                if (me.orig == null || me.orig.mainTexture == null || me.orig.mainTexture == me.tex.srcMain || !Eligible(me.orig)) continue;
                try
                {
                    Tex nt = GetTex(me.orig, me.crop);
                    if (nt == me.tex) continue;
                    me.tex.mats.Remove(me);
                    me.tex = nt;
                    nt.mats.Add(me);
                    ShowMat(me);
                    stampSeq++;
                    if (logs++ < 30) Debug.Log("[FirstPersonLoD] VR stone HD: " + me.orig.name + " changed texture (new floor); its first-person copy follows: " + nt.name);
                }
                catch (Exception) { }
            }
            try
            {
                if (lbm == null && Kami.Inst != null) lbm = Kami.Inst.GetComponent<LevelBlockMod>();
                if (lbm != null)
                    foreach (KeyValuePair<string, int> kv in cropUse)
                    {
                        if (kv.Value < 40) continue;
                        Vector4 c = cropByKey[kv.Key];
                        For(lbm.BlockNorm, c); For(lbm.Blockalt, c); For(lbm.BlockNoMoss, c); For(lbm.Dark, c);
                    }
            }
            catch (Exception) { }
        }
        while (queue.Count > 0 && running == null)
        {
            Job j = queue[0];
            queue.RemoveAt(0);
            if (j.gen != j.e.gen || j.e.ready) { continue; }
            try { Start(j); lastWork = now; }
            catch (Exception ex)
            {
                running = null;
                j.e.failed = true; j.e.queued = false;
                Debug.Log("[FirstPersonLoD] VR stone HD: " + j.e.name + " could not be read (" + ex.GetType().Name + ": " + ex.Message + "); the game's texture stays");
            }
        }
        // nothing built for 20 s: let the work buffers go (tens of MB in a 32-bit game)
        if (running == null && queue.Count == 0 && (pool.Count > 0 || fpool.Count > 0) && now - lastWork > 20f)
        {
            pool.Clear(); fpool.Clear();
        }
    }

    // which other surfaces a room is drawn with: logged once per material and texture
    private static readonly Dictionary<string, bool> seenMats = new Dictionary<string, bool>();
    public static void Census(GameObject room)
    {
        if (!FPConfig.StoneHD || room == null || seenMats.Count > 120) return;
        try
        {
            Renderer[] rs = room.GetComponentsInChildren<Renderer>(false);
            StringBuilder sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < rs.Length && n < 25; i++)
            {
                Renderer r = rs[i];
                if (r == null || !(r is MeshRenderer) || r.name == "FPBrick") continue;
                Material[] ms = r.sharedMaterials;
                for (int k = 0; k < ms.Length; k++)
                {
                    Material m = ms[k];
                    if (m == null || byHd.ContainsKey(m.GetInstanceID())) continue;
                    Texture t = m.mainTexture;
                    string key = m.name + "|" + (t != null ? t.name : "-");
                    if (seenMats.ContainsKey(key)) continue;
                    seenMats[key] = true;
                    n++;
                    Transform p = r.transform.parent;
                    sb.Append("\n  " + m.name + " (" + (m.shader != null ? m.shader.name : "?") + ") texture " + (t != null ? t.name + " " + t.width + "x" + t.height + " " + t.filterMode : "none")
                        + " on " + (p != null ? p.name + "/" : "") + r.name + (Eligible(m) ? " [HD eligible]" : ""));
                }
            }
            if (n > 0) Debug.Log("[FirstPersonLoD] VR stone HD: surfaces in " + room.name + " not yet seen:" + sb.ToString());
        }
        catch (Exception) { }
    }

    public static string Note()
    {
        int ready = 0, pending = 0, cold = 0;
        foreach (Tex e in texes.Values) { if (e.ready) ready++; else if (e.queued) pending++; else if (!e.failed) cold++; }
        return ready + " HD texture sets ready, " + pending + " building" + (cold > 0 ? ", " + cold + " set aside" : "") + (show ? "" : ", game textures shown (F1)");
    }
}

// ---------------------------------------------------------------------------------------------
// Rough masonry (first person only; BrickBatch draws the result instead of the game's bricks while
// first person drives). Every brick in the game is the same bevelled 1x1x2 box. Each is given:
//   - one of a few rough versions of the brick mesh (subdivided, faces pushed in and out with
//     smooth noise, corners chipped), randomly flipped end for end
//   - a small random nudge, tilt and size change, so walls and floors stop reading as a grid
//   - optionally (BrickGloss) one of three shininess levels of its material: damp and dry stones
// BrickRough scales the shape and placement changes (0 = off, 1 = default, 2 = rubble). With
// StoneHD the bricks also get the HD stone textures (see StoneHD). Collision is untouched (the game collides with invisible
// blocks, not the bricks). Everything is seeded from each brick's position, so a room always comes
// out the same. Vertex budget per room (BrickMaxVerts) keeps memory in check: this is a 32-bit
// game, so a very large room gets a lighter version of the rough bricks.
// ---------------------------------------------------------------------------------------------
public static class RoughBricks
{
    private static readonly Dictionary<string, Mesh[]> sets = new Dictionary<string, Mesh[]>();
    private static readonly Dictionary<string, Material[]> glossy = new Dictionary<string, Material[]>();
    private const int Variants = 6;

    private static float Hash(int x, int y, int z, int s)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177 + s * 1911520717);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    private static float Noise(Vector3 p, int s)
    {
        int x0 = Mathf.FloorToInt(p.x), y0 = Mathf.FloorToInt(p.y), z0 = Mathf.FloorToInt(p.z);
        float fx = p.x - x0, fy = p.y - y0, fz = p.z - z0;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy); fz = fz * fz * (3f - 2f * fz);
        float a = Mathf.Lerp(Hash(x0, y0, z0, s), Hash(x0 + 1, y0, z0, s), fx);
        float b = Mathf.Lerp(Hash(x0, y0 + 1, z0, s), Hash(x0 + 1, y0 + 1, z0, s), fx);
        float c = Mathf.Lerp(Hash(x0, y0, z0 + 1, s), Hash(x0 + 1, y0, z0 + 1, s), fx);
        float d = Mathf.Lerp(Hash(x0, y0 + 1, z0 + 1, s), Hash(x0 + 1, y0 + 1, z0 + 1, s), fx);
        return Mathf.Lerp(Mathf.Lerp(a, b, fy), Mathf.Lerp(c, d, fy), fz);
    }

    private static Mesh Build(Mesh src, int level, int seed, float strength, bool remap, Vector4 crop)
    {
        List<Vector3> v = new List<Vector3>(src.vertices);
        Vector2[] suv = src.uv;
        List<Vector2> uv = new List<Vector2>(suv != null && suv.Length == v.Count ? suv : new Vector2[v.Count]);
        List<int> t = new List<int>(src.triangles);
        for (int l = 0; l < level; l++)
        {
            Dictionary<long, int> mid = new Dictionary<long, int>();
            List<int> nt = new List<int>(t.Count * 4);
            for (int i = 0; i < t.Count; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                int ab = Mid(a, b, v, uv, mid), bc = Mid(b, c, v, uv, mid), ca = Mid(c, a, v, uv, mid);
                nt.Add(a); nt.Add(ab); nt.Add(ca);
                nt.Add(ab); nt.Add(b); nt.Add(bc);
                nt.Add(ca); nt.Add(bc); nt.Add(c);
                nt.Add(ab); nt.Add(bc); nt.Add(ca);
            }
            t = nt;
        }
        Bounds bo = src.bounds;
        Vector3 ctr = bo.center, ext = bo.extents;
        float amp = 0.07f * strength;                  // in the mesh's own units (the brick is 1 x 1 x 2)
        for (int i = 0; i < v.Count; i++)
        {
            Vector3 p = v[i];
            // push along the direction out of the brick's middle (the same for every copy of a
            // corner, so the surface never tears where faces meet)
            Vector3 q = new Vector3((p.x - ctr.x) / Mathf.Max(ext.x, 1e-4f), (p.y - ctr.y) / Mathf.Max(ext.y, 1e-4f), (p.z - ctr.z) / Mathf.Max(ext.z, 1e-4f));
            Vector3 dir = new Vector3(q.x * q.x * q.x, q.y * q.y * q.y, q.z * q.z * q.z);   // the face this point belongs to
            if (dir.sqrMagnitude < 1e-6f) continue;
            dir.Normalize();
            float lump = Noise(p * 2.3f + new Vector3(seed * 7.1f, 0f, 0f), seed) - 0.5f;          // broad lumps
            float grain = Noise(p * 7.0f + new Vector3(0f, seed * 3.3f, 0f), seed + 101) - 0.5f;   // small pits
            float corner = Mathf.Max(0f, Mathf.Abs(q.x) + Mathf.Abs(q.y) + Mathf.Abs(q.z) - 2.4f);  // near a corner
            float chip = corner > 0f ? -corner * (0.6f + Hash(Mathf.RoundToInt(p.x * 4), Mathf.RoundToInt(p.y * 4), Mathf.RoundToInt(p.z * 4), seed)) : 0f;
            float disp = amp * (0.9f * lump + 0.45f * grain) + amp * 0.9f * chip - amp * 0.25f;   // net a touch smaller: mortar gaps
            v[i] = p + dir * disp;
        }
        if (remap)   // HD stone: the part of the texture the brick uses becomes the whole texture
            for (int i = 0; i < uv.Count; i++) uv[i] = new Vector2((uv[i].x - crop.x) / crop.z, (uv[i].y - crop.y) / crop.w);
        Mesh m = new Mesh();
        m.name = src.name + (strength > 0f ? "_rough" + seed : "_plain") + (remap ? "_hd" : "");
        m.SetVertices(v);
        m.SetUVs(0, uv);
        m.SetTriangles(t, 0);
        m.RecalculateNormals();
        m.RecalculateTangents();
        m.RecalculateBounds();
        return m;
    }

    private static int Mid(int a, int b, List<Vector3> v, List<Vector2> uv, Dictionary<long, int> mid)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        int r;
        if (mid.TryGetValue(key, out r)) return r;
        v.Add((v[a] + v[b]) * 0.5f);
        uv.Add((uv[a] + uv[b]) * 0.5f);
        r = v.Count - 1;
        mid[key] = r;
        return r;
    }

    private static Mesh[] Set(Mesh src, int level) { return Set(src, level, false); }
    private static Mesh[] Set(Mesh src, int level, bool remap)
    {
        float strength = FPConfig.BrickRough;
        if (strength <= 0f) level = 0;
        Vector4 crop = remap ? StoneHD.Crop(src) : Vector4.zero;
        string key = src.GetInstanceID() + "/" + level + "/" + strength.ToString("0.00") + (remap ? "/hd" + crop.ToString("F4") : "");
        Mesh[] s;
        if (sets.TryGetValue(key, out s)) return s;
        s = new Mesh[strength > 0f ? Variants : 1];
        for (int i = 0; i < s.Length; i++) s[i] = Build(src, level, i + 1, strength, remap, crop);
        sets[key] = s;
        return s;
    }

    private static Material Gloss(Material m, int pick)
    {
        if (m == null || !m.HasProperty("_Glossiness")) return m;
        // an HD material changes its texture when the HD one is ready: key it by itself only
        string key = StoneHD.IsOurs(m) ? "hd" + m.GetInstanceID() : m.GetInstanceID() + "/" + (m.mainTexture != null ? m.mainTexture.GetInstanceID() : 0);
        Material[] g;
        if (!glossy.TryGetValue(key, out g))
        {
            g = new Material[3];
            float baseG = m.GetFloat("_Glossiness");
            float[] mul = { 0.45f, 1f, 1.5f };
            for (int i = 0; i < 3; i++)
            {
                g[i] = new Material(m);
                g[i].name = m.name + "_gloss" + i;
                g[i].SetFloat("_Glossiness", Mathf.Clamp(baseG * mul[i], 0.05f, 0.85f));
                StoneHD.Follow(g[i], m);
            }
            glossy[key] = g;
        }
        return g[pick];
    }

    // after every first-person brick is gone (BrickBatch.Rebuild): meshes and shine copies go too
    public static void Reset()
    {
        foreach (Mesh[] set in sets.Values) for (int i = 0; i < set.Length; i++) if (set[i] != null) UnityEngine.Object.Destroy(set[i]);
        sets.Clear();
        foreach (Material[] g in glossy.Values)
            for (int i = 0; i < g.Length; i++) if (g[i] != null) { StoneHD.Unfollow(g[i]); UnityEngine.Object.Destroy(g[i]); }
        glossy.Clear();
    }

    private static int Seed(Vector3 wp)
    {
        return (Mathf.RoundToInt(wp.x * 100f) * 73856093 ^ Mathf.RoundToInt(wp.y * 100f) * 19349663 ^ Mathf.RoundToInt(wp.z * 100f) * 83492791) & 0x7fffffff;
    }

    // moving bricks: which rough variant (the same one every time for this brick)
    public static int Variant(Transform orig)
    {
        if (FPConfig.BrickRough <= 0f) return 0;
        return new System.Random(Seed(orig.position)).Next(Variants);
    }

    public static Mesh MeshFor(Mesh src, int level, int variant, bool remap)
    {
        if (src == null || !src.isReadable || (FPConfig.BrickRough <= 0f && !remap)) return src;
        Mesh[] set = Set(src, FPConfig.BrickRough > 0f ? level : 0, remap);
        return set[variant % set.Length];
    }

    // detail level for a room of this many bricks (vertex budget; 32-bit game)
    public static int Level(Mesh probe, int count)
    {
        int level = Mathf.Clamp(FPConfig.BrickDetail, 0, 3);
        if (FPConfig.BrickRough > 0f && probe != null && probe.isReadable)
            while (level > 0 && (long)count * Set(probe, level, StoneHD.Active)[0].vertexCount > FPConfig.BrickMaxVerts) level--;
        return level;
    }

    // the first-person version of one brick: rough mesh, placement jitter (applied only to static
    // bricks' copies), HD stone material and shine. Seeded from the brick's position, so a room
    // always comes out the same.
    public static void Pick(Transform orig, Mesh srcMesh, Material srcMat, int level, bool gloss,
        out Mesh mesh, out Material mat, out Vector3 localPos, out Quaternion localRot, out float scale,
        ref int verts, ref int hdCount, Dictionary<string, int> hdMats)
    {
        float s = FPConfig.BrickRough;
        Vector3 wp = orig.position;
        System.Random rnd = new System.Random(Seed(wp));
        Material hd = null;
        if (StoneHD.Active && srcMesh.isReadable)
        {
            Vector4 crop = StoneHD.Crop(srcMesh);
            hd = StoneHD.For(srcMat, crop);
            if (hd != null)
            {
                StoneHD.CountUse(crop);
                hdCount++;
                int hc; hdMats.TryGetValue(hd.name, out hc); hdMats[hd.name] = hc + 1;
            }
        }
        mesh = srcMesh;
        if (srcMesh.isReadable && (s > 0f || hd != null))
        {
            Mesh[] set = Set(srcMesh, s > 0f ? level : 0, hd != null);
            mesh = set[s > 0f ? rnd.Next(set.Length) : 0];
        }
        verts += mesh.vertexCount;
        localPos = Vector3.zero; localRot = Quaternion.identity; scale = 1f;
        if (s > 0f)
        {
            if (rnd.Next(2) == 1) localRot = Quaternion.Euler(0f, 0f, 180f);   // end over end
            float n = 0.022f * s;   // nudge, in the tile's units (a brick is half a unit thick)
            Vector3 nudge = new Vector3((float)(rnd.NextDouble() * 2 - 1) * n, (float)(rnd.NextDouble() * 2 - 1) * n, (float)(rnd.NextDouble() * 2 - 1) * n);
            float tilt = 2.2f * s;
            localRot = localRot * Quaternion.Euler((float)(rnd.NextDouble() * 2 - 1) * tilt, (float)(rnd.NextDouble() * 2 - 1) * tilt, (float)(rnd.NextDouble() * 2 - 1) * tilt);
            scale = 1f + (float)(rnd.NextDouble() * 2 - 1) * 0.04f * s;
            // the nudge is in the tile's space; the copy lives in the brick's own space
            Vector3 world = orig.parent != null ? orig.parent.TransformVector(nudge) : nudge;
            localPos = orig.InverseTransformVector(world);
        }
        mat = hd != null ? hd : srcMat;
        bool metal = false;
        if (hd == null && Metal.Is(srcMat)) { Material mm = Metal.For(srcMat); if (mm != null) { mat = mm; metal = true; hdCount++; int hc; hdMats.TryGetValue(mm.name, out hc); hdMats[mm.name] = hc + 1; } }
        // metal keeps its own shine (specular); the gloss variants are for stone
        if (gloss && FPConfig.BrickGloss && mat != null && !metal) mat = Gloss(mat, rnd.Next(3));
    }
}

// ---------------------------------------------------------------------------------------------
// First-person bricks. The tabletop view (and pause, menus) always draws the game's own bricks,
// untouched. First person draws its own version (RoughBricks: rough mesh, placement jitter and
// shine; StoneHD: HD stone), and switching view flips between the two:
//   static bricks  plain SmallBlock bricks in tiles without moving parts get a copy: a child object
//                  of the game's brick (so it switches off and is destroyed with it) with the
//                  first-person look, static-batched in groups (one combined mesh per material
//                  per group). First person hides the game's brick and shows the copy.
//   moving bricks  every other stone brick (Block* texture: moving walls, stairs, switches, fire
//                  boxes) is changed in place while first person drives: rough mesh and HD stone,
//                  no jitter, so it keeps moving, switching on and off and changing material with
//                  the game (a switch lighting up gets the matching HD material). Tabletop gets the
//                  game's mesh and material back.
// A room is prepared over several frames (BrickBudgetMs each) on first entry, so entering a room
// does not stall; until then it shows the game's bricks.
// Changing BrickRough or StoneHDStrength with the keys rebuilds every room.
// ---------------------------------------------------------------------------------------------
public static class BrickBatch
{
    private sealed class Pair
    {
        public Renderer orig;
        public MeshRenderer copy;        // static bricks
        public bool moving;
        public MeshFilter mf;            // moving bricks: mesh swapped, HD stone by property block
        public Mesh srcMesh;
        public int variant, level;
        public Material lastMat;
        public Texture lastMain;
        public int stamp = -1;
        public bool fp;
    }
    private sealed class RoomJob
    {
        public GameObject room;
        public int ceiling;
        public readonly List<Transform> still = new List<Transform>();
        public readonly List<MeshRenderer> moving = new List<MeshRenderer>();
        public readonly List<Pair> made = new List<Pair>();
        public readonly List<GameObject> batch = new List<GameObject>();
        public int next, batched, level, verts, hd, stage, tiles, skipped;
        public readonly Dictionary<string, int> hdMats = new Dictionary<string, int>(), perMat = new Dictionary<string, int>();
        public float started;
        public double ms;
        public int frames;
    }

    private static readonly Dictionary<int, bool> done = new Dictionary<int, bool>();
    private static readonly List<GameObject> retries = new List<GameObject>();
    private static readonly Dictionary<int, int> attempts = new Dictionary<int, int>();
    private static readonly List<RoomJob> jobs = new List<RoomJob>();
    private static readonly List<Pair> pairs = new List<Pair>();
    private static readonly List<Pair> movingPairs = new List<Pair>();
    private static readonly Dictionary<int, bool> movingIds = new Dictionary<int, bool>();
    private static float nextRetry, nextSync, nextSweep;
    private static readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
    // meshes made by static batching, per room: Unity keeps them after their renderers are gone
    private static readonly List<GameObject> batchRooms = new List<GameObject>();
    private static readonly List<List<Mesh>> batchMeshes = new List<List<Mesh>>();
    private static bool fpShown = true;
    private static int logged;

    // rooms that were still switched off when first entered: prepare them once they are on
    public static void Retry()
    {
        if (retries.Count == 0 || Time.realtimeSinceStartup < nextRetry) return;
        nextRetry = Time.realtimeSinceStartup + 0.5f;
        for (int i = retries.Count - 1; i >= 0; i--)
        {
            GameObject g = retries[i];
            if (g == null) { retries.RemoveAt(i); continue; }
            if (g.activeInHierarchy) { RoomShape.Room(g); Room(g); }
        }
    }

    private static bool PlainBrick(Transform b)
    {
        if (b.name != "SmallBlock" || !b.gameObject.activeInHierarchy) return false;
        Component[] cs = b.GetComponents<Component>();
        MeshRenderer mr = null;
        for (int i = 0; i < cs.Length; i++)
        {
            Component c = cs[i];
            if (c == null) return false;
            if (c is Transform || c is MeshFilter || c is BoxCollider) continue;
            if (c is MeshRenderer) { mr = (MeshRenderer)c; continue; }
            // an empty marker script nothing in the game reads (bricks that could have become a
            // floor switch); a brick with MusicTag stays in place: the music listens to whether its
            // renderer is seen
            if (c.GetType().Name == "couldbeafloorswitch") continue;
            return false;
        }
        if (mr == null || !mr.enabled) return false;
        MeshFilter mf = b.GetComponent<MeshFilter>();
        return mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable;
    }

    // the first test PlainBrick fails, for the log
    private static string PlainReason(Transform b)
    {
        if (b.name != "SmallBlock") return "named " + b.name;
        if (!b.gameObject.activeInHierarchy) return "switched off";
        Component[] cs = b.GetComponents<Component>();
        for (int i = 0; i < cs.Length; i++)
        {
            Component c = cs[i];
            if (c == null) return "missing script";
            if (c is Transform || c is MeshFilter || c is BoxCollider || c is MeshRenderer || c.GetType().Name == "couldbeafloorswitch") continue;
            return "carries " + c.GetType().Name;
        }
        MeshRenderer mr = b.GetComponent<MeshRenderer>();
        if (mr == null) return "no renderer";
        if (!mr.enabled) return "renderer off";
        MeshFilter mf = b.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return "no mesh";
        if (!mf.sharedMesh.isReadable) return "mesh not readable";
        return "plain";
    }

    // why a brick is not a plain one (for the log)
    private static string Why(Transform b)
    {
        StringBuilder sb = new StringBuilder();
        Component[] cs = b.GetComponents<Component>();
        for (int i = 0; i < cs.Length; i++) sb.Append(cs[i] == null ? " (missing script)" : " " + cs[i].GetType().Name);
        MeshRenderer mr = b.GetComponent<MeshRenderer>();
        MeshFilter mf = b.GetComponent<MeshFilter>();
        return b.name + ":" + sb + (mr != null && !mr.enabled ? ", renderer off" : "") + (mf != null && mf.sharedMesh != null && !mf.sharedMesh.isReadable ? ", mesh not readable" : "");
    }

    // any other stone brick: one material with a Block* texture on a readable mesh
    private static bool StoneBrick(MeshRenderer mr)
    {
        if (mr == null || !mr.enabled || mr.name == "FPBrick" || movingIds.ContainsKey(mr.GetInstanceID())) return false;
        Material[] ms = mr.sharedMaterials;
        if (ms.Length != 1 || ms[0] == null || StoneHD.IsOurs(ms[0])) return false;
        Texture t = ms[0].mainTexture;
        if (t == null || !t.name.StartsWith("Block")) return false;
        MeshFilter mf = mr.GetComponent<MeshFilter>();
        return mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable && mr.GetComponent<TextMesh>() == null && mr.GetComponent<MeshCollider>() == null;
    }

    private static bool StaticParent(Transform t)
    {
        if (t.GetComponent<Rigidbody>() != null || t.GetComponent<Animation>() != null || t.GetComponent<Animator>() != null) return false;
        // tiles the game moves from script (switch-driven walls and floors)
        MonoBehaviour[] mbs = t.GetComponents<MonoBehaviour>();
        for (int i = 0; i < mbs.Length; i++)
        {
            if (mbs[i] == null) continue;
            string n = mbs[i].GetType().Name;
            if (n == "MoveSwitchable" || n == "moveFloorSwitch" || n == "iTween" || n.StartsWith("Move") || n.StartsWith("move")) return false;
        }
        return true;
    }

    public static void Room(GameObject room)
    {
        if (!FPConfig.MergeBricks || room == null) return;
        int id = room.GetInstanceID();
        if (done.ContainsKey(id)) return;
        // not switched on yet (the first room of a run, while the game sets it up): everything in
        // it reads as switched off, which used to leave every brick "moving" (unmerged, slow).
        // Wait for it; Retry prepares it (room shape first) once it is on.
        if (!room.activeInHierarchy)
        {
            if (!retries.Contains(room) && retries.Count < 50)
            {
                retries.Add(room);
                if (logged++ < 60) Debug.Log("[FirstPersonLoD] VR bricks: " + room.name + " is not switched on yet; preparing it once it is");
            }
            return;
        }
        done[id] = true;
        try
        {
            RoomJob j = new RoomJob();
            j.room = room;
            j.started = Time.realtimeSinceStartup;
            System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
            Transform r = room.transform;
            // arriving by stairs, a new floor's rooms are prepared a moment before the game switches
            // their bricks on (renderers off): wait for that, or every brick would count as moving
            int bricksSeen = 0, bricksOff = 0;
            for (int i = 0; i < r.childCount; i++)
            {
                Transform tile = r.GetChild(i);
                for (int k = 0; k < tile.childCount; k++)
                {
                    Transform b = tile.GetChild(k);
                    if (b.name != "SmallBlock" || !b.gameObject.activeInHierarchy) continue;
                    MeshRenderer bm = b.GetComponent<MeshRenderer>();
                    if (bm == null) continue;
                    bricksSeen++;
                    if (!bm.enabled) bricksOff++;
                }
            }
            if (bricksSeen > 0 && bricksOff * 3 > bricksSeen)
            {
                int tries0; attempts.TryGetValue(id, out tries0);
                if (tries0 < 30)
                {
                    attempts[id] = tries0 + 1; done.Remove(id);
                    if (!retries.Contains(room)) retries.Add(room);
                    if (tries0 == 0 && logged++ < 40) Debug.Log("[FirstPersonLoD] VR bricks: " + room.name + ": " + bricksOff + " of " + bricksSeen + " bricks are not drawn yet; waiting for the game to switch them on");
                    return;
                }
            }
            // doorway passages first, so their new bricks are collected (HD, rough) with the rest
            Doorways.Room(room);
            Dictionary<int, bool> taken = new Dictionary<int, bool>();
            for (int i = 0; i < r.childCount; i++)
            {
                Transform tile = r.GetChild(i);
                if (PlainBrick(tile)) { j.still.Add(tile); taken[tile.GetInstanceID()] = true; continue; }
                if (!StaticParent(tile)) { j.skipped++; continue; }
                j.tiles++;
                for (int k = 0; k < tile.childCount; k++)
                {
                    Transform b = tile.GetChild(k);
                    if (!PlainBrick(b)) continue;
                    j.still.Add(b); taken[b.GetInstanceID()] = true;
                    Material m = b.GetComponent<MeshRenderer>().sharedMaterial;
                    string key = m != null ? m.name : "none";
                    int c; j.perMat.TryGetValue(key, out c); j.perMat[key] = c + 1;
                }
            }
            MeshRenderer[] all = room.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < all.Length; i++)
                if (!taken.ContainsKey(all[i].transform.GetInstanceID()) && StoneBrick(all[i])) j.moving.Add(all[i]);
            // most of the room's plain bricks ended up "in place": something about this moment (seen
            // on the first room of a floor, arriving by stairs) - say what, and try again shortly
            if (j.moving.Count > 40 && j.still.Count == 0)
            {
                int plainLooking = 0;
                Dictionary<string, int> reasons = new Dictionary<string, int>();
                for (int i = 0; i < j.moving.Count; i++)
                {
                    Transform b = j.moving[i].transform;
                    string why0 = PlainReason(b);
                    Transform par = b.parent;
                    string where = par == null ? "no parent" : par == r ? "directly in the room" : par.parent == r ? (StaticParent(par) ? "static tile" : "moving tile") : "deeper (" + par.name + " under " + (par.parent != null ? par.parent.name : "?") + ")";
                    string k = why0 + ", " + where;
                    int c; reasons.TryGetValue(k, out c); reasons[k] = c + 1;
                    if (why0 == "plain") plainLooking++;
                }
                int tries1; attempts.TryGetValue(id, out tries1);
                StringBuilder rs0 = new StringBuilder();
                int shown0 = 0;
                foreach (KeyValuePair<string, int> kv in reasons) { if (shown0++ >= 6) break; rs0.Append("\n  " + kv.Value + " x " + kv.Key); }
                if (plainLooking * 2 > j.moving.Count && tries1 < 6)
                {
                    attempts[id] = tries1 + 1; done.Remove(id);
                    if (!retries.Contains(room)) retries.Add(room);
                    if (logged++ < 60) Debug.Log("[FirstPersonLoD] VR bricks: " + room.name + ": none of " + j.moving.Count + " bricks came out mergeable (try " + (tries1 + 1) + "); trying again in half a second:" + rs0);
                    return;
                }
                if (logged++ < 60) Debug.Log("[FirstPersonLoD] VR bricks: " + room.name + ": bricks kept in place after " + tries1 + " retries:" + rs0);
            }
            if (j.moving.Count > 40 && j.moving.Count > j.still.Count && logged++ < 60)
            {
                Dictionary<string, int> why = new Dictionary<string, int>();
                for (int i = 0; i < j.moving.Count; i++) { string k = Why(j.moving[i].transform); int c; why.TryGetValue(k, out c); why[k] = c + 1; }
                StringBuilder sb = new StringBuilder();
                int shown = 0;
                foreach (KeyValuePair<string, int> kv in why) { if (shown++ >= 6) break; sb.Append("\n  " + kv.Value + " x " + kv.Key); }
                Debug.Log("[FirstPersonLoD] VR bricks: " + room.name + ": " + j.moving.Count + " bricks kept in place (not merged), by what they carry:" + sb);
            }
            if (j.still.Count + j.moving.Count < 2)
            {
                // a room that is not switched on yet (arriving through a door or stairs) has no active
                // bricks: try again once it is, instead of leaving it for good
                int asleep = 0;
                for (int i = 0; i < r.childCount && asleep == 0; i++)
                {
                    Transform tile = r.GetChild(i);
                    for (int k = 0; k < tile.childCount; k++) if (tile.GetChild(k).name == "SmallBlock" && !tile.GetChild(k).gameObject.activeInHierarchy) { asleep++; break; }
                }
                int tries; attempts.TryGetValue(id, out tries);
                if (asleep > 0 && tries < 10 && retries.Count < 50) { attempts[id] = tries + 1; done.Remove(id); if (!retries.Contains(room)) retries.Add(room); }
                else retries.Remove(room);
                if (logged++ < 20) Debug.Log("[FirstPersonLoD] VR bricks: " + room.name + " has no bricks to prepare" + (asleep > 0 ? " yet (room not switched on); will retry" : ""));
                return;
            }
            retries.Remove(room);
            Mesh probe = null;
            for (int i = 0; i < j.still.Count && probe == null; i++) probe = j.still[i].GetComponent<MeshFilter>().sharedMesh;
            for (int i = 0; i < j.moving.Count && probe == null; i++) { MeshFilter pf = j.moving[i].GetComponent<MeshFilter>(); if (pf != null) probe = pf.sharedMesh; }
            // ceiling bricks are seen far less (and from further away): they count half toward the
            // vertex budget that picks the room's brick detail
            int ceilingBricks = 0;
            for (int i = 0; i < j.still.Count; i++) if (RoomShape.IsCeiling(j.still[i])) ceilingBricks++;
            j.ceiling = ceilingBricks;
            j.level = RoughBricks.Level(probe, j.still.Count + j.moving.Count - ceilingBricks / 2);
            j.ms += w.Elapsed.TotalMilliseconds;
            jobs.Add(j);
            Statues.Room(room);
            Props.Room(room);
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] VR bricks: preparing " + room.name + " failed, the game's bricks stay: " + e.GetType().Name + ": " + e.Message); }
    }

    private static Pair MakeStill(RoomJob j, Transform b)
    {
        MeshRenderer omr = b.GetComponent<MeshRenderer>();
        MeshFilter omf = b.GetComponent<MeshFilter>();
        if (omr == null || omf == null || omf.sharedMesh == null) return null;
        Mesh mesh; Material mat; Vector3 lp; Quaternion lr; float sc;
        RoughBricks.Pick(b, omf.sharedMesh, omr.sharedMaterial, j.level, true, out mesh, out mat, out lp, out lr, out sc, ref j.verts, ref j.hd, j.hdMats);
        GameObject g = new GameObject("FPBrick");
        g.layer = b.gameObject.layer;
        Transform t = g.transform;
        t.SetParent(b, false);
        t.localPosition = lp; t.localRotation = lr; t.localScale = Vector3.one * sc;
        g.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer mr = g.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = omr.shadowCastingMode;
        mr.receiveShadows = omr.receiveShadows;
        mr.lightProbeUsage = omr.lightProbeUsage;
        mr.reflectionProbeUsage = omr.reflectionProbeUsage;
        mr.enabled = false;   // shown with the rest of the room when it is ready
        Pair p = new Pair();
        p.orig = omr; p.copy = mr;
        return p;
    }

    private static Pair MakeMoving(RoomJob j, MeshRenderer mr)
    {
        MeshFilter mf = mr.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return null;
        Pair p = new Pair();
        p.orig = mr; p.moving = true; p.mf = mf;
        p.srcMesh = mf.sharedMesh; p.level = j.level;
        p.variant = RoughBricks.Variant(mr.transform);
        Material m = mr.sharedMaterial;
        if (StoneHD.TexFor(m, StoneHD.Crop(p.srcMesh)) != null) j.hd++;
        Mesh mesh = RoughBricks.MeshFor(p.srcMesh, p.level, p.variant, StoneHD.Active && StoneHD.Eligible(m));
        if (mesh != null) j.verts += mesh.vertexCount;
        return p;
    }

    // moving bricks keep the game's material (the game may tint it, swap it or make its own copy);
    // first person swaps the mesh and lays the HD stone over it with a property block
    private static void ApplyMoving(Pair p, bool on)
    {
        if (p.orig == null || p.mf == null) return;
        if (on)
        {
            Material m = p.orig.sharedMaterial;
            StoneHD.Tex te = m != null && p.srcMesh.isReadable ? StoneHD.TexFor(m, StoneHD.Crop(p.srcMesh)) : null;
            p.mf.sharedMesh = RoughBricks.MeshFor(p.srcMesh, p.level, p.variant, te != null);
            if (te != null) { StoneHD.Fill(mpb, te); p.orig.SetPropertyBlock(mpb); }
            else p.orig.SetPropertyBlock(StoneHD.Empty);
            p.lastMat = m; p.lastMain = m != null ? m.mainTexture : null; p.stamp = StoneHD.Stamp;
            p.fp = true;
        }
        else
        {
            if (!p.fp) return;
            p.mf.sharedMesh = p.srcMesh;
            p.orig.SetPropertyBlock(StoneHD.Empty);
            p.fp = false;
        }
    }

    // a few ms of room preparation per frame (first person only)
    public static void Tick()
    {
        Retry();
        SyncMoving();
        Statues.Tick(VRFP.CurrentRoom);
        Props.Tick(VRFP.CurrentRoom);
        FPTorches.Tick();
        if (jobs.Count == 0) return;
        RoomJob j = jobs[0];
        if (j.room == null) { jobs.RemoveAt(0); return; }
        System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
        double budget = Mathf.Max(1f, FPConfig.BrickBudgetMs);
        j.frames++;
        try
        {
            int total = j.still.Count + j.moving.Count;
            while (j.stage == 0 && w.Elapsed.TotalMilliseconds < budget)
            {
                if (j.next >= total) { j.stage = 1; break; }
                bool moving = j.next >= j.still.Count;
                Pair p;
                if (moving)
                {
                    MeshRenderer mr = j.moving[j.next - j.still.Count];
                    j.next++;
                    if (mr == null || movingIds.ContainsKey(mr.GetInstanceID())) continue;
                    p = MakeMoving(j, mr);
                }
                else
                {
                    Transform b = j.still[j.next];
                    j.next++;
                    if (b == null) continue;
                    p = MakeStill(j, b);
                    if (p != null) j.batch.Add(p.copy.gameObject);
                }
                if (p != null) j.made.Add(p);
            }
            if (j.stage == 1)
            {
                // everything made: flip the whole room at once
                for (int i = 0; i < j.made.Count; i++)
                {
                    Pair p = j.made[i];
                    if (p.orig == null || (!p.moving && p.copy == null)) continue;
                    pairs.Add(p);
                    if (p.moving)
                    {
                        movingIds[p.orig.GetInstanceID()] = true;
                        movingPairs.Add(p);
                        ApplyMoving(p, fpShown);
                    }
                    else
                    {
                        p.copy.enabled = fpShown;
                        p.orig.enabled = !fpShown;
                    }
                }
                j.stage = 2;
            }
            // static batching in groups, one or two per frame (a combined mesh per material per group)
            while (j.stage == 2 && w.Elapsed.TotalMilliseconds < budget)
            {
                if (j.batched >= j.batch.Count || !fpShown || !FPConfig.BrickStaticBatch) { j.stage = 3; break; }
                int n = Math.Min(Mathf.Max(20, FPConfig.BrickBatchGroup), j.batch.Count - j.batched);
                List<GameObject> grp = new List<GameObject>(n);
                for (int i = j.batched; i < j.batched + n; i++) if (j.batch[i] != null) grp.Add(j.batch[i]);
                j.batched += n;
                if (grp.Count > 1)
                {
                    StaticBatchingUtility.Combine(grp.ToArray(), j.room);
                    int ri = batchRooms.IndexOf(j.room);
                    if (ri < 0) { batchRooms.Add(j.room); batchMeshes.Add(new List<Mesh>()); ri = batchRooms.Count - 1; }
                    for (int i = 0; i < grp.Count; i++)
                    {
                        MeshFilter f = grp[i] != null ? grp[i].GetComponent<MeshFilter>() : null;
                        Mesh cm = f != null ? f.sharedMesh : null;
                        if (cm != null && cm.name.StartsWith("Combined Mesh") && !batchMeshes[ri].Contains(cm)) batchMeshes[ri].Add(cm);
                    }
                }
            }
            j.ms += w.Elapsed.TotalMilliseconds;
            if (j.stage < 3) return;
            jobs.RemoveAt(0);
            StoneHD.Census(j.room);
            if (logged++ < 60)
            {
                StringBuilder mats = new StringBuilder();
                foreach (KeyValuePair<string, int> kv in j.perMat) mats.Append(" " + kv.Key + " x" + kv.Value);
                StringBuilder hdm = new StringBuilder();
                foreach (KeyValuePair<string, int> kv in j.hdMats) hdm.Append(" " + kv.Key + " x" + kv.Value);
                Debug.Log("[FirstPersonLoD] VR bricks: " + j.room.name + ": first-person bricks for " + j.still.Count + " bricks in " + j.tiles + " tiles (" + mats.ToString().Trim() + ")" + (j.ceiling > 0 ? " (" + j.ceiling + " of them ceiling)" : "")
                    + (j.moving.Count > 0 ? " and " + j.moving.Count + " stone pieces of moving parts, switches and stairs" : "")
                    + "; rough masonry strength " + FPConfig.BrickRough.ToString("0.##") + ", detail " + j.level + ", " + (j.verts / 1000) + "k vertices"
                    + (FPConfig.BrickGloss ? ", 3 shine levels" : "")
                    + (StoneHD.Active ? "; HD stone on " + j.hd + " (" + hdm.ToString().Trim() + "; " + StoneHD.Note() + ")" : "")
                    + "; " + j.ms.ToString("0") + " ms of work spread over " + j.frames + " frames (" + (Time.realtimeSinceStartup - j.started).ToString("0.00") + " s)"
                    + (FPConfig.BrickStaticBatch ? ", merged in " + Mathf.CeilToInt(j.batch.Count / (float)Mathf.Max(20, FPConfig.BrickBatchGroup)) + " groups" : ", not merged")
                    + "; the tabletop view keeps the game's bricks");
            }
        }
        catch (Exception e)
        {
            jobs.RemoveAt(0);
            Debug.Log("[FirstPersonLoD] VR bricks: preparing " + (j.room != null ? j.room.name : "?") + " failed part way: " + e.GetType().Name + ": " + e.Message);
            for (int i = 0; i < j.made.Count; i++)
            {
                Pair p = j.made[i];
                if (pairs.Contains(p)) continue;
                if (p.copy != null) Kill(p.copy.gameObject);
            }
        }
    }

    // moving bricks: follow the game's material and texture changes and HD textures arriving
    // (checked ten times a second); rooms that are gone give back their merged meshes
    private static void SyncMoving()
    {
        float now = Time.realtimeSinceStartup;
        if (now >= nextSweep)
        {
            nextSweep = now + 5f;
            for (int i = batchRooms.Count - 1; i >= 0; i--)
                if (batchRooms[i] == null) { DestroyMeshes(batchMeshes[i]); batchRooms.RemoveAt(i); batchMeshes.RemoveAt(i); }
        }
        if (!fpShown || movingPairs.Count == 0 || now < nextSync) return;
        nextSync = now + 0.1f;
        int stamp = StoneHD.Stamp;
        for (int i = movingPairs.Count - 1; i >= 0; i--)
        {
            Pair p = movingPairs[i];
            if (p.orig == null || p.mf == null) { movingPairs.RemoveAt(i); continue; }
            Material m = p.orig.sharedMaterial;
            if (m != p.lastMat || (m != null && m.mainTexture != p.lastMain) || p.stamp != stamp) ApplyMoving(p, true);
        }
    }

    private static void DestroyMeshes(List<Mesh> ms)
    {
        for (int i = 0; i < ms.Count; i++) if (ms[i] != null) UnityEngine.Object.Destroy(ms[i]);
        ms.Clear();
    }

    // first person on: the first-person bricks; tabletop, pause and menus: the game's own bricks
    public static void Show(bool on)
    {
        Statues.Show(on);
        Props.Show(on);
        Doorways.Show(on);
        if (fpShown == on) return;
        fpShown = on;
        for (int i = pairs.Count - 1; i >= 0; i--)
        {
            Pair p = pairs[i];
            if (p.orig == null) { pairs.RemoveAt(i); continue; }
            if (!p.moving && p.copy == null) { p.orig.enabled = true; pairs.RemoveAt(i); continue; }   // copy gone: the game's brick
            if (p.moving) ApplyMoving(p, on);
            else { p.copy.enabled = on; p.orig.enabled = !on; }
        }
        for (int i = movingPairs.Count - 1; i >= 0; i--) if (movingPairs[i].orig == null) movingPairs.RemoveAt(i);
    }

    private static void Kill(GameObject g)
    {
        // out of the brick first, so a rescan in the same frame does not see it
        g.name = "FPBrickGone";
        g.transform.SetParent(null, false);
        UnityEngine.Object.Destroy(g);
    }

    // new strength: every first-person brick goes, rooms are prepared again (yours right away)
    public static void Rebuild(GameObject current)
    {
        for (int i = 0; i < pairs.Count; i++)
        {
            Pair p = pairs[i];
            if (p.moving) { ApplyMoving(p, false); continue; }
            if (p.orig != null) p.orig.enabled = true;
            if (p.copy != null) Kill(p.copy.gameObject);
        }
        for (int k = 0; k < jobs.Count; k++)
            for (int i = 0; i < jobs[k].made.Count; i++) if (jobs[k].made[i].copy != null && !pairs.Contains(jobs[k].made[i])) Kill(jobs[k].made[i].copy.gameObject);
        pairs.Clear(); movingPairs.Clear(); movingIds.Clear(); jobs.Clear(); done.Clear(); attempts.Clear(); retries.Clear();
        for (int i = 0; i < batchMeshes.Count; i++) DestroyMeshes(batchMeshes[i]);
        batchRooms.Clear(); batchMeshes.Clear();
        RoughBricks.Reset();
        if (current != null) Room(current);
    }
}

// flickering for the mod's own torches (the Tavern's corner torches): light intensity and flame
// size follow smooth noise, each torch on its own
public static class FPTorches
{
    private static readonly List<Light> lights = new List<Light>();
    private static readonly List<Transform> flames = new List<Transform>();
    private static readonly List<float> base0 = new List<float>();
    private static readonly List<Vector3> scale0 = new List<Vector3>();

    public static void Add(Light l, Transform flame)
    {
        lights.Add(l); flames.Add(flame); base0.Add(l.intensity); scale0.Add(flame != null ? flame.localScale : Vector3.one);
    }

    public static void Tick()
    {
        float t = Time.time;
        for (int i = lights.Count - 1; i >= 0; i--)
        {
            Light l = lights[i];
            if (l == null) { lights.RemoveAt(i); flames.RemoveAt(i); base0.RemoveAt(i); scale0.RemoveAt(i); continue; }
            if (!l.isActiveAndEnabled) continue;
            float n = Mathf.PerlinNoise(t * 3.1f + i * 7.3f, i * 1.7f) * 0.7f + Mathf.PerlinNoise(t * 9.7f + i * 3.1f, 5f + i) * 0.3f;
            l.intensity = base0[i] * (0.82f + 0.36f * n);
            Transform f = flames[i];
            if (f != null) f.localScale = new Vector3(scale0[i].x * (0.9f + 0.2f * n), scale0[i].y * (0.8f + 0.45f * n), scale0[i].z * (0.9f + 0.2f * n));
        }
    }
}

// ---------------------------------------------------------------------------------------------
// 3D statues and angels (first person only). The angel statues on pillars and the angels that
// come out of the walls to fight are flat, double-sided picture cards (the item quad). First
// person carves each one in stone from its own picture: the outline smoothed on a grid three
// times finer than the picture (no pixel staircases), a chamfered edge all round and the body
// raised a little above the wings, StatueDepth pixels at the most on each side (StoneCore.
// CarveGrid). The picture itself is turned to stone once per texture in the background
// (StoneCore.StatueBake: its shading kept, colour mostly drained, stone grain, pits and hairline
// cracks, dark lines carved as grooves) and laid on with a property block, so the game's own
// material (and anything the game does to it: tint, flashes) stays; the tabletop view gets the
// flat card back. Animated figures get one shape per animation frame, swapped as the game changes
// frames. Chosen by name (StatueNames) or texture name (StatueTextures); the log lists each one.
// ---------------------------------------------------------------------------------------------
public static class Statues
{
    private sealed class Item
    {
        public MeshRenderer r;
        public MeshFilter mf;
        public Mesh src, shape;
        public Vector3 scale0;
        public Material lastMat;
        public Texture lastMain;
        public Vector2 lastS, lastO;
        public int stamp = -1;
        public bool fp;
    }
    private static readonly List<Item> items = new List<Item>();
    private static readonly Dictionary<int, bool> seen = new Dictionary<int, bool>();
    private static readonly Dictionary<string, Mesh> shapes = new Dictionary<string, Mesh>();
    private static readonly Dictionary<int, byte[]> pixels = new Dictionary<int, byte[]>();
    private static readonly Dictionary<int, int[]> sizes = new Dictionary<int, int[]>();
    private static readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
    private static bool shown = true;
    private static float nextScan;
    private static int logged, built;

    // the name lists, split once (the room is rescanned often: no garbage per renderer)
    private static string[] names, texNames;
    private static string namesSrc, texSrc;
    private static string[] Split(string list)
    {
        List<string> o = new List<string>();
        string[] parts = list.Split(',');
        for (int i = 0; i < parts.Length; i++) { string k = parts[i].Trim(); if (k.Length > 0) o.Add(k); }
        return o.ToArray();
    }
    private static bool Listed(string n, string[] want)
    {
        for (int i = 0; i < want.Length; i++) if (n.IndexOf(want[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static readonly Dictionary<int, bool> rejected = new Dictionary<int, bool>();
    private static bool Wanted(MeshRenderer r)
    {
        if (names == null || namesSrc != FPConfig.StatueNames) { namesSrc = FPConfig.StatueNames; names = Split(namesSrc); }
        if (texNames == null || texSrc != FPConfig.StatueTextures) { texSrc = FPConfig.StatueTextures; texNames = Split(texSrc); }
        if (Listed(r.name, names)) return true;
        Material m = r.sharedMaterial;
        return m != null && m.mainTexture != null && Listed(m.mainTexture.name, texNames);
    }

    public static void Room(GameObject room)
    {
        if (!FPConfig.Statues3D || room == null) return;
        MeshRenderer[] rs = room.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            MeshRenderer r = rs[i];
            if (r == null) continue;
            int rid = r.GetInstanceID();
            if (seen.ContainsKey(rid) || rejected.ContainsKey(rid)) continue;
            if (!Wanted(r)) { if (rejected.Count > 20000) rejected.Clear(); rejected[rid] = true; continue; }
            seen[r.GetInstanceID()] = true;
            try { Add(r); }
            catch (Exception e) { Debug.Log("[FirstPersonLoD] VR statue " + r.name + ": left flat (" + e.GetType().Name + ": " + e.Message + ")"); }
        }
    }

    private static void Add(MeshRenderer r)
    {
        MeshFilter mf = r.GetComponent<MeshFilter>();
        Material m = r.sharedMaterial;
        if (mf == null || mf.sharedMesh == null || m == null || m.mainTexture == null) return;
        Mesh src = mf.sharedMesh;
        Bounds mb = src.bounds;
        if (src.vertexCount > 8 || mb.size.z > 0.01f || mb.size.x < 0.5f || mb.size.y < 0.5f)
        {
            if (logged++ < 20) Debug.Log("[FirstPersonLoD] VR statue " + r.name + ": not a flat card (" + src.name + ", " + src.vertexCount + " vertices, size " + mb.size.ToString("F2") + "), left as it is");
            return;
        }
        Item it = new Item();
        it.r = r; it.mf = mf; it.src = src; it.scale0 = r.transform.localScale;
        items.Add(it);
        if (shown) Apply(it, true);
        if (logged++ < 30)
        {
            Transform p = r.transform.parent;
            Debug.Log("[FirstPersonLoD] VR statue " + (p != null ? p.name + "/" : "") + r.name + ": carved 3D figure from " + m.mainTexture.name + " (" + m.mainTexture.width + "x" + m.mainTexture.height
                + ", frame " + m.mainTextureScale.ToString("F3") + " at " + m.mainTextureOffset.ToString("F3") + ")" + (it.shape != null ? ", " + it.shape.vertexCount + " vertices" : ", no shape"));
        }
    }

    // The carved figure for one picture frame (StoneCore.CarveGrid): a grid three times finer than
    // the picture, the outline smoothed, a chamfered edge and a slightly raised body, the same at
    // the back; thin side walls along the outline. Positions in the card's own space, texture
    // coordinates in the texture's space (the frame is baked in).
    private static Mesh Shape(Texture tex, Vector2 S, Vector2 O, Vector3 scale)
    {
        // animation sheets step their offset past 1 and rely on the texture repeating: bring the
        // frame into the first tile
        O = new Vector2(O.x - Mathf.Floor(O.x + Mathf.Min(0f, S.x) + 1e-4f), O.y - Mathf.Floor(O.y + Mathf.Min(0f, S.y) + 1e-4f));
        string key = tex.GetInstanceID() + "/" + S.ToString("F4") + O.ToString("F4") + "/" + scale.ToString("F3") + "/" + FPConfig.StatueDepth.ToString("0.00");
        Mesh m;
        if (shapes.TryGetValue(key, out m)) return m;
        byte[] px = Pixels(tex);
        int tid = tex.GetInstanceID();
        int tw = sizes[tid][0], th = sizes[tid][1];
        int fw = Mathf.RoundToInt(Mathf.Abs(S.x) * tw), fh = Mathf.RoundToInt(Mathf.Abs(S.y) * th);
        if (fw < 2 || fh < 2 || fw > 256 || fh > 256) { shapes[key] = null; return null; }
        float[] alpha = new float[fw * fh];
        for (int cj = 0; cj < fh; cj++)
            for (int ci = 0; ci < fw; ci++)
            {
                int tx = Mathf.FloorToInt((O.x + (ci + 0.5f) / fw * S.x) * tw), ty = Mathf.FloorToInt((O.y + (cj + 0.5f) / fh * S.y) * th);
                tx = ((tx % tw) + tw) % tw; ty = ((ty % th) + th) % th;
                alpha[cj * fw + ci] = px[(ty * tw + tx) * 4 + 3] / 255f;
            }
        float pxWorld = 0.5f * (Mathf.Abs(scale.x) / fw + Mathf.Abs(scale.y) / fh);
        float pz = pxWorld / Mathf.Max(0.01f, Mathf.Abs(scale.z));
        float D = Mathf.Max(0.2f, FPConfig.StatueDepth);
        for (int U = Mathf.Clamp(96 / Mathf.Max(fw, fh), 1, 3); U >= 1; U--)
        {
            bool[] inside; float[] hc; int cells;
            StoneCore.CarveGrid(alpha, fw, fh, U, D, out inside, out hc, out cells);
            if (cells < 3 * U * U) { shapes[key] = null; return null; }
            m = Build(inside, hc, fw, fh, U, S, O, pz);
            if (m != null) break;
        }
        shapes[key] = m;
        if (m != null) built++;
        return m;
    }

    private static Mesh Build(bool[] inside, float[] hc, int fw, int fh, int U, Vector2 S, Vector2 O, float pz)
    {
        int GW = fw * U, GH = fh * U, CW = GW + 1;
        int[] vf = new int[CW * (GH + 1)], vb = new int[CW * (GH + 1)], wt = new int[CW * (GH + 1)], wb = new int[CW * (GH + 1)];
        for (int i = 0; i < vf.Length; i++) { vf[i] = -1; vb[i] = -1; wt[i] = -1; wb[i] = -1; }
        List<Vector3> v = new List<Vector3>(); List<Vector2> uv = new List<Vector2>(); List<int> t = new List<int>();
        float fu = 1f / GW, fv = 1f / GH;
        for (int j = 0; j < GH; j++)
            for (int i = 0; i < GW; i++)
            {
                if (!inside[j * GW + i]) continue;
                int c00 = j * CW + i, c10 = c00 + 1, c01 = c00 + CW, c11 = c01 + 1;
                int[] cs = { c00, c10, c11, c01 };
                for (int k = 0; k < 4; k++)
                {
                    int c = cs[k];
                    if (vf[c] >= 0) continue;
                    int gx = c % CW, gy = c / CW;
                    float cu = gx * fu, cv = gy * fv, h = Mathf.Max(0f, hc[c]) * pz;
                    Vector2 tuv = new Vector2(O.x + cu * S.x, O.y + cv * S.y);
                    vf[c] = v.Count; v.Add(new Vector3(0.5f - cu, cv - 0.5f, h)); uv.Add(tuv);
                    vb[c] = v.Count; v.Add(new Vector3(0.5f - cu, cv - 0.5f, -h)); uv.Add(tuv);
                }
                // split along the flatter diagonal
                bool diagA = Mathf.Abs(hc[c00] - hc[c11]) <= Mathf.Abs(hc[c10] - hc[c01]);
                if (diagA)
                {
                    Tri(v, t, vf[c00], vf[c10], vf[c11], 1f); Tri(v, t, vf[c00], vf[c11], vf[c01], 1f);
                    Tri(v, t, vb[c00], vb[c10], vb[c11], -1f); Tri(v, t, vb[c00], vb[c11], vb[c01], -1f);
                }
                else
                {
                    Tri(v, t, vf[c00], vf[c10], vf[c01], 1f); Tri(v, t, vf[c10], vf[c11], vf[c01], 1f);
                    Tri(v, t, vb[c00], vb[c10], vb[c01], -1f); Tri(v, t, vb[c10], vb[c11], vb[c01], -1f);
                }
            }
        // side walls where a stone cell meets an open one (x runs opposite to the picture's u)
        for (int j = 0; j < GH; j++)
            for (int i = 0; i < GW; i++)
            {
                if (!inside[j * GW + i]) continue;
                int c00 = j * CW + i, c10 = c00 + 1, c01 = c00 + CW, c11 = c01 + 1;
                if (i == 0 || !inside[j * GW + i - 1]) Wall(v, uv, t, wt, wb, inside, hc, GW, GH, c00, c01, new Vector3(1f, 0f, 0f), fu, fv, S, O, pz);
                if (i == GW - 1 || !inside[j * GW + i + 1]) Wall(v, uv, t, wt, wb, inside, hc, GW, GH, c10, c11, new Vector3(-1f, 0f, 0f), fu, fv, S, O, pz);
                if (j == 0 || !inside[(j - 1) * GW + i]) Wall(v, uv, t, wt, wb, inside, hc, GW, GH, c00, c10, new Vector3(0f, -1f, 0f), fu, fv, S, O, pz);
                if (j == GH - 1 || !inside[(j + 1) * GW + i]) Wall(v, uv, t, wt, wb, inside, hc, GW, GH, c01, c11, new Vector3(0f, 1f, 0f), fu, fv, S, O, pz);
            }
        if (v.Count > 65000) return null;
        Mesh m = new Mesh();
        m.name = "statue_carved";
        m.SetVertices(v);
        m.SetUVs(0, uv);
        m.SetTriangles(t, 0);
        m.RecalculateNormals();
        m.RecalculateTangents();
        m.RecalculateBounds();
        return m;
    }

    private static void Tri(List<Vector3> v, List<int> t, int a, int b, int c, float facing)
    {
        // Unity faces a triangle toward cross(b - a, c - a)
        if (Vector3.Cross(v[b] - v[a], v[c] - v[a]).z * facing >= 0f) { t.Add(a); t.Add(b); t.Add(c); }
        else { t.Add(a); t.Add(c); t.Add(b); }
    }

    // one wall piece between two outline corners; its corners are shared along the outline, so the
    // wall shades smoothly round the smoothed outline. Its texture is the stone just inside.
    private static void Wall(List<Vector3> v, List<Vector2> uv, List<int> t, int[] wt, int[] wb, bool[] inside, float[] hc, int GW, int GH,
        int p, int q, Vector3 facing, float fu, float fv, Vector2 S, Vector2 O, float pz)
    {
        int CW = GW + 1;
        int[] ends = { p, q };
        for (int e = 0; e < 2; e++)
        {
            int c = ends[e];
            if (wt[c] >= 0) continue;
            int gx = c % CW, gy = c / CW;
            // the stone cells around this corner: their centres, for the texture
            float su = 0f, sv = 0f; int n = 0;
            for (int dj = -1; dj <= 0; dj++)
                for (int di = -1; di <= 0; di++)
                {
                    int ci = gx + di, cj = gy + dj;
                    if (ci < 0 || cj < 0 || ci >= GW || cj >= GH || !inside[cj * GW + ci]) continue;
                    su += (ci + 0.5f) * fu; sv += (cj + 0.5f) * fv; n++;
                }
            if (n == 0) { su = gx * fu; sv = gy * fv; n = 1; }
            Vector2 tuv = new Vector2(O.x + su / n * S.x, O.y + sv / n * S.y);
            float h = Mathf.Max(0f, hc[c]) * pz;
            float cu = gx * fu, cv = gy * fv;
            wt[c] = v.Count; v.Add(new Vector3(0.5f - cu, cv - 0.5f, h)); uv.Add(tuv);
            wb[c] = v.Count; v.Add(new Vector3(0.5f - cu, cv - 0.5f, -h)); uv.Add(tuv);
        }
        int a = wt[p], b = wt[q], c2 = wb[q], d = wb[p];
        if (Vector3.Dot(Vector3.Cross(v[b] - v[a], v[c2] - v[a]), facing) >= 0f) { t.Add(a); t.Add(b); t.Add(c2); t.Add(a); t.Add(c2); t.Add(d); }
        else { t.Add(a); t.Add(c2); t.Add(b); t.Add(a); t.Add(d); t.Add(c2); }
    }

    private static byte[] Pixels(Texture tex)
    {
        int tid = tex.GetInstanceID();
        byte[] px;
        if (!pixels.TryGetValue(tid, out px))
        {
            int tw, th;
            px = StoneHD.ReadPixels(tex, out tw, out th);
            pixels[tid] = px; sizes[tid] = new int[] { tw, th };
        }
        return px;
    }

    // the whole texture turned to stone (StoneCore.StatueBake), made in the background
    private sealed class Bake { public FPTex.Job job; public byte[] a, n; public int w, h, k; public Texture2D ta, tn; public bool failed; }
    private static readonly Dictionary<int, Bake> bakes = new Dictionary<int, Bake>();
    private static int bakeStamp;

    private static Bake BakeFor(Texture tex)
    {
        int tid = tex.GetInstanceID();
        Bake b;
        if (bakes.TryGetValue(tid, out b)) return b;
        b = new Bake();
        bakes[tid] = b;
        try
        {
            byte[] px = Pixels(tex);
            int tw = sizes[tid][0], th = sizes[tid][1];
            b.k = Mathf.Clamp(1024 / Mathf.Max(tw, th), 2, 16);
            b.w = tw * b.k; b.h = th * b.k;
            b.a = FPTex.Chain(b.w, b.h); b.n = FPTex.Chain(b.w, b.h);
            Bake B = b;
            b.job = FPTex.Run(delegate ()
            {
                StoneCore.StatueBake(px, tw, th, B.k, B.a, B.n);
                FPTex.Mip(B.a, B.w, B.h, false); FPTex.Mip(B.n, B.w, B.h, true);
            });
        }
        catch (Exception e) { b.failed = true; Debug.Log("[FirstPersonLoD] VR statue: turning " + tex.name + " to stone failed: " + e.GetType().Name + ": " + e.Message); }
        return b;
    }

    private static void Apply(Item it, bool on)
    {
        if (it.r == null || it.mf == null) return;
        if (on)
        {
            Material m = it.r.sharedMaterial;
            Mesh shape = m != null && m.mainTexture != null ? Shape(m.mainTexture, m.mainTextureScale, m.mainTextureOffset, it.scale0) : null;
            it.lastMat = m; it.lastMain = m != null ? m.mainTexture : null;
            it.lastS = m != null ? m.mainTextureScale : Vector2.zero; it.lastO = m != null ? m.mainTextureOffset : Vector2.zero;
            it.stamp = bakeStamp;
            if (shape == null)
            {
                // this frame has no shape (empty or odd): the game's card as it is
                if (it.fp) { it.mf.sharedMesh = it.src; it.r.SetPropertyBlock(StoneHD.Empty); it.fp = false; }
                return;
            }
            it.shape = shape;
            it.mf.sharedMesh = shape;
            Bake b = BakeFor(m.mainTexture);
            mpb.Clear();
            if (b.ta != null)
            {
                mpb.SetTexture("_MainTex", b.ta);
                mpb.SetTexture("_BumpMap", b.tn);
            }
            else mpb.SetTexture("_MainTex", m.mainTexture);
            mpb.SetVector("_MainTex_ST", new Vector4(1f, 1f, 0f, 0f));
            mpb.SetVector("_BumpMap_ST", new Vector4(1f, 1f, 0f, 0f));
            it.r.SetPropertyBlock(mpb);
            it.fp = true;
        }
        else
        {
            if (!it.fp) return;
            it.mf.sharedMesh = it.src;
            it.r.SetPropertyBlock(StoneHD.Empty);
            it.fp = false;
        }
    }

    public static void Show(bool on)
    {
        if (shown == on) return;
        shown = on;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].r == null) { items.RemoveAt(i); continue; }
            Apply(items[i], on);
        }
    }

    // every first-person frame: follow animation frames and material changes, stone arriving;
    // every 1.5 s look for new figures in the room (angels coming out of the walls)
    public static void Tick(GameObject room)
    {
        if (!FPConfig.Statues3D) return;
        foreach (KeyValuePair<int, Bake> kv in bakes)
        {
            Bake b = kv.Value;
            if (b.job == null || !b.job.done) continue;
            FPTex.Job j = b.job; b.job = null;
            if (j.error != null) { b.failed = true; Debug.Log("[FirstPersonLoD] VR statue: stone texture failed: " + j.error); break; }
            try
            {
                b.ta = FPTex.Make(b.a, b.w, b.h, false, "FPStatueStone", TextureWrapMode.Clamp, 8);
                b.tn = FPTex.Make(b.n, b.w, b.h, true, "FPStatueStoneNormal", TextureWrapMode.Clamp, 8);
                Debug.Log("[FirstPersonLoD] VR statue: stone texture " + b.w + "x" + b.h + " (" + b.k + " per picture pixel) made in " + j.ms + " ms (background)");
            }
            catch (Exception e) { b.failed = true; Debug.Log("[FirstPersonLoD] VR statue: stone texture upload failed: " + e.GetType().Name + ": " + e.Message); }
            b.a = b.n = null;
            bakeStamp++;
            break;   // one upload per frame
        }
        if (!shown) return;
        if (room != null && Time.realtimeSinceStartup >= nextScan)
        {
            nextScan = Time.realtimeSinceStartup + 1.5f;
            Room(room);
        }
        for (int i = items.Count - 1; i >= 0; i--)
        {
            Item it = items[i];
            if (it.r == null) { items.RemoveAt(i); continue; }
            Material m = it.r.sharedMaterial;
            if (m == null) continue;
            if (m != it.lastMat || m.mainTexture != it.lastMain || m.mainTextureScale != it.lastS || m.mainTextureOffset != it.lastO || it.stamp != bakeStamp
                || (it.fp && it.mf.sharedMesh != it.shape))
                Apply(it, true);
        }
    }

    public static string Note() { return items.Count + " figures, " + built + " carved shapes"; }
}

// ---------------------------------------------------------------------------------------------
// Textures made by the mod: background work (pure C#, no Unity calls) and the upload after it.
// ---------------------------------------------------------------------------------------------
public static class FPTex
{
    public sealed class Job { public volatile bool done; public string error; public long ms; }
    public delegate void Work();

    public static Job Run(Work work)
    {
        Job j = new Job();
        System.Threading.Thread t = new System.Threading.Thread(delegate ()
        {
            System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
            try { work(); }
            catch (Exception e) { j.error = e.GetType().Name + ": " + e.Message; }
            j.ms = w.ElapsedMilliseconds;
            j.done = true;
        });
        t.IsBackground = true;
        t.Priority = System.Threading.ThreadPriority.BelowNormal;
        t.Start();
        return j;
    }

    public static int Levels(int w, int h) { int l = 1, m = Math.Max(w, h); while (m > 1) { m >>= 1; l++; } return l; }
    // a buffer for a whole mip chain; level 0 is written at its start
    public static byte[] Chain(int w, int h) { return new byte[StoneCore.MipBytes(w, h, Levels(w, h))]; }
    public static void Mip(byte[] chain, int w, int h, bool normal) { StoneCore.Mips(chain, w, h, Levels(w, h), normal); }

    // main thread: a finished chain to a texture (no CPU copy kept)
    public static Texture2D Make(byte[] chain, int w, int h, bool normal, string name, TextureWrapMode wrap, int aniso)
    {
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, true, normal);
        int levels = Levels(w, h);
        if (t.mipmapCount == levels && chain.Length == StoneCore.MipBytes(w, h, levels)) { t.LoadRawTextureData(chain); t.Apply(false, true); }
        else
        {
            Color32[] c = new Color32[w * h];
            for (int i = 0; i < c.Length; i++) c[i] = new Color32(chain[i * 4], chain[i * 4 + 1], chain[i * 4 + 2], chain[i * 4 + 3]);
            t.SetPixels32(c);
            t.Apply(true, true);
        }
        t.wrapMode = wrap;
        t.filterMode = FilterMode.Trilinear;
        t.anisoLevel = aniso;
        t.name = name;
        return t;
    }

    public static Shader Find(params string[] names)
    {
        for (int i = 0; i < names.Length; i++) { Shader s = Shader.Find(names[i]); if (s != null && s.isSupported) return s; }
        return null;
    }
}

// ---------------------------------------------------------------------------------------------
// Metal blocks (first person only). The iron blocks are the brick shape in a plain grey material
// with no texture at all. Their first-person copies get a metal plate instead: a tileable 256 px
// texture (brushed streaks, mottled wear, pits, scratches; StoneCore.Metal) and its normal map, one
// plate per brick face, on a legacy bumped-specular shader, tinted with the game's own colour.
// Materials named like MetalNames (untextured) are treated.
// ---------------------------------------------------------------------------------------------
public static class Metal
{
    private static Texture2D alb, nrm;
    private static readonly Dictionary<int, Material> made = new Dictionary<int, Material>();
    private static bool failed;

    public static bool Is(Material m)
    {
        if (!FPConfig.MetalBlocks || m == null || m.mainTexture != null) return false;
        string[] names = FPConfig.MetalNames.Split(',');
        for (int i = 0; i < names.Length; i++) { string k = names[i].Trim(); if (k.Length > 0 && m.name.StartsWith(k, StringComparison.OrdinalIgnoreCase)) return true; }
        return false;
    }

    public static Material For(Material m)
    {
        if (failed) return null;
        Material r;
        if (made.TryGetValue(m.GetInstanceID(), out r) && r != null) return r;
        try
        {
            if (alb == null)
            {
                System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                const int N = 256;
                byte[] a = FPTex.Chain(N, N), n = FPTex.Chain(N, N);
                StoneCore.Metal(N, a, n);
                FPTex.Mip(a, N, N, false); FPTex.Mip(n, N, N, true);
                alb = FPTex.Make(a, N, N, false, "FPMetal", TextureWrapMode.Repeat, 8);
                nrm = FPTex.Make(n, N, N, true, "FPMetalNormal", TextureWrapMode.Repeat, 8);
                Debug.Log("[FirstPersonLoD] VR metal blocks: plate texture made in " + w.ElapsedMilliseconds + " ms");
            }
            Shader sh = FPTex.Find("Legacy Shaders/Bumped Specular", "Legacy Shaders/Transparent/Cutout/Bumped Specular", "Legacy Shaders/Bumped Diffuse");
            if (sh == null) { failed = true; Debug.Log("[FirstPersonLoD] VR metal blocks: no bumped shader in the game; left plain"); return null; }
            r = new Material(sh);
            r.name = m.name + "_metal";
            r.color = m.HasProperty("_Color") ? m.color : new Color(0.6f, 0.6f, 0.62f);
            r.mainTexture = alb;
            r.SetTexture("_BumpMap", nrm);
            // the brick's texture coordinates cover one eighth of the texture per face: one plate a face
            r.mainTextureScale = new Vector2(8f, 8f);
            r.SetTextureScale("_BumpMap", new Vector2(8f, 8f));
            if (r.HasProperty("_SpecColor")) r.SetColor("_SpecColor", new Color(0.55f, 0.55f, 0.6f));
            if (r.HasProperty("_Shininess")) r.SetFloat("_Shininess", 0.25f);
            if (r.HasProperty("_Cutoff")) r.SetFloat("_Cutoff", 0.01f);
            made[m.GetInstanceID()] = r;
            Debug.Log("[FirstPersonLoD] VR metal blocks: " + m.name + " gets iron plate (" + sh.name + ")");
            return r;
        }
        catch (Exception e) { failed = true; Debug.Log("[FirstPersonLoD] VR metal blocks failed: " + e.GetType().Name + ": " + e.Message); return null; }
    }
}

// ---------------------------------------------------------------------------------------------
// Crates (first person only). The game's crate is a plain cube with an 8 x 8 picture: a light
// border (the iron binding) around dark boards. First person builds the real thing on a child of
// the crate: three boards a side, each its own piece with a gap to the dark inside, flat-sawn
// grain with arches, fibres, knots and worn edges (StoneCore.CratePlanks), held in iron angle
// bars along all twelve edges, standing proud of the boards, rivets along them, brushed and
// scaled with a little rust (StoneCore.CrateStrap), on a shiny shader. Colours come from the
// game's own picture (boards from the middle, iron from the border, drained to grey). The game's
// crate is only hidden (its physics, breaking and pushing are untouched) and its colour is copied
// across, so a crate the game tints still shows it. Tabletop draws the game's crate.
// Chosen by texture name (PropWood); rooms are rescanned every 1.5 s for new ones.
// ---------------------------------------------------------------------------------------------
public static class Props
{
    private sealed class Item { public Renderer r; public Texture tex; public GameObject fp; public MeshRenderer fr; public bool hidden; public Color lastColor; public bool colorSet; }
    private sealed class Look
    {
        public FPTex.Job job; public byte[] wa, wn, sa, sn;
        public Material src, wood, steel; public bool failed; public string colours; public long jobMs;
    }
    private static readonly List<Item> items = new List<Item>();
    private static readonly Dictionary<int, bool> seen = new Dictionary<int, bool>();
    private static readonly Dictionary<int, Look> looks = new Dictionary<int, Look>();
    private static readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
    private static Mesh crateMesh;
    private delegate Vector2 UvMap(Vector3 x);
    private static bool shown = true;
    private static float nextScan;
    private static int logged;
    private static string[] wood; private static string woodSrc;
    const int PW = 256, PH = 512, SW = 128, SH = 256;

    private static bool Wood(Texture t)
    {
        if (t == null) return false;
        if (wood == null || woodSrc != FPConfig.PropWood)
        {
            woodSrc = FPConfig.PropWood;
            List<string> o = new List<string>();
            foreach (string p in woodSrc.Split(',')) { string k = p.Trim(); if (k.Length > 0) o.Add(k); }
            wood = o.ToArray();
        }
        for (int i = 0; i < wood.Length; i++) if (t.name.Equals(wood[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static void Room(GameObject room)
    {
        if (!FPConfig.Crates3D || room == null) return;
        MeshRenderer[] rs = room.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            MeshRenderer r = rs[i];
            if (r == null || r.name == "FPCrate") continue;
            int id = r.GetInstanceID();
            if (seen.ContainsKey(id)) continue;
            if (seen.Count > 50000) seen.Clear();
            seen[id] = true;
            Material m = r.sharedMaterial;
            if (m == null || !Wood(m.mainTexture)) continue;
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount > 24 || Mathf.Abs(mf.sharedMesh.bounds.size.x - 1f) > 0.05f)
            {
                if (logged++ < 10) Debug.Log("[FirstPersonLoD] VR crates: " + r.name + " is not the plain cube (" + (mf != null && mf.sharedMesh != null ? mf.sharedMesh.name + ", " + mf.sharedMesh.vertexCount + " vertices" : "no mesh") + "), left as the game's");
                continue;
            }
            Item it = new Item(); it.r = r; it.tex = m.mainTexture;
            items.Add(it);
            LookFor(m);
            Apply(it, shown);
            if (logged++ < 10) Debug.Log("[FirstPersonLoD] VR crates: " + (r.transform.parent != null ? r.transform.parent.name + "/" : "") + r.name + ": built as a 3D crate (boards and iron binding) from " + m.mainTexture.name);
        }
    }

    private static Look LookFor(Material m)
    {
        int tid = m.mainTexture.GetInstanceID();
        Look lk;
        if (looks.TryGetValue(tid, out lk)) return lk;
        lk = new Look(); lk.src = m;
        looks[tid] = lk;
        try
        {
            int w, h;
            byte[] px = StoneHD.ReadPixels(m.mainTexture, out w, out h);
            // boards: the middle of the picture; iron: its outer ring
            double ir = 0, ig = 0, ib = 0, br = 0, bg = 0, bb = 0; int ni = 0, nb = 0;
            int ring = Math.Max(1, w / 8);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool border = x < ring || y < ring || x >= w - ring || y >= h - ring;
                    if (border) { br += px[o]; bg += px[o + 1]; bb += px[o + 2]; nb++; }
                    else { ir += px[o]; ig += px[o + 1]; ib += px[o + 2]; ni++; }
                }
            float wr = (float)(ir / Math.Max(1, ni) / 255.0), wg = (float)(ig / Math.Max(1, ni) / 255.0), wb = (float)(ib / Math.Max(1, ni) / 255.0);
            float sr = (float)(br / Math.Max(1, nb) / 255.0), sg = (float)(bg / Math.Max(1, nb) / 255.0), sb = (float)(bb / Math.Max(1, nb) / 255.0);
            // boards a little brighter than the picture's (the grain darkens them again)
            float lift = 1.15f;
            wr = Mathf.Clamp01(wr * lift); wg = Mathf.Clamp01(wg * lift); wb = Mathf.Clamp01(wb * lift);
            // iron: the border's brightness, most of its colour drained, not too pale
            float l = 0.3f * sr + 0.59f * sg + 0.11f * sb;
            float il = Mathf.Clamp(l * 0.85f, 0.3f, 0.55f);
            float ir2 = il + (sr - l) * 0.2f, ig2 = il + (sg - l) * 0.2f, ib2 = il + (sb - l) * 0.2f + 0.015f;
            lk.colours = "boards (" + wr.ToString("0.00") + ", " + wg.ToString("0.00") + ", " + wb.ToString("0.00") + "), iron (" + ir2.ToString("0.00") + ", " + ig2.ToString("0.00") + ", " + ib2.ToString("0.00") + ")";
            lk.wa = FPTex.Chain(PW, PH); lk.wn = FPTex.Chain(PW, PH); lk.sa = FPTex.Chain(SW, SH); lk.sn = FPTex.Chain(SW, SH);
            Look L = lk;
            lk.job = FPTex.Run(delegate ()
            {
                StoneCore.CratePlanks(PW, PH, wr, wg, wb, L.wa, L.wn);
                FPTex.Mip(L.wa, PW, PH, false); FPTex.Mip(L.wn, PW, PH, true);
                StoneCore.CrateStrap(SW, SH, ir2, ig2, ib2, L.sa, L.sn);
                FPTex.Mip(L.sa, SW, SH, false); FPTex.Mip(L.sn, SW, SH, true);
            });
        }
        catch (Exception e) { lk.failed = true; Debug.Log("[FirstPersonLoD] VR crates: " + m.mainTexture.name + ": could not read the picture (" + e.GetType().Name + ": " + e.Message + "); crates stay the game's"); }
        return lk;
    }

    private static void Finish(Look lk)
    {
        lk.job = null;
        try
        {
            Texture2D wa = FPTex.Make(lk.wa, PW, PH, false, "FPCrateBoards", TextureWrapMode.Clamp, 8);
            Texture2D wn = FPTex.Make(lk.wn, PW, PH, true, "FPCrateBoardsNormal", TextureWrapMode.Clamp, 8);
            Texture2D sa = FPTex.Make(lk.sa, SW, SH, false, "FPCrateIron", TextureWrapMode.Repeat, 8);
            Texture2D sn = FPTex.Make(lk.sn, SW, SH, true, "FPCrateIronNormal", TextureWrapMode.Repeat, 8);
            lk.wa = lk.wn = lk.sa = lk.sn = null;
            Material w = new Material(lk.src);
            w.name = "crate_fp_boards";
            w.mainTexture = wa; w.mainTextureScale = Vector2.one; w.mainTextureOffset = Vector2.zero;
            if (w.HasProperty("_BumpMap")) { w.SetTexture("_BumpMap", wn); w.SetTextureScale("_BumpMap", Vector2.one); w.SetTextureOffset("_BumpMap", Vector2.zero); }
            if (w.HasProperty("_Color")) w.color = Color.white;
            Shader sh = FPTex.Find("Legacy Shaders/Bumped Specular", "Legacy Shaders/Transparent/Cutout/Bumped Specular");
            Material s = sh != null ? new Material(sh) : new Material(w);
            s.name = "crate_fp_iron";
            s.mainTexture = sa; s.mainTextureScale = Vector2.one; s.mainTextureOffset = Vector2.zero;
            if (s.HasProperty("_BumpMap")) s.SetTexture("_BumpMap", sn);
            if (s.HasProperty("_Color")) s.color = Color.white;
            if (s.HasProperty("_SpecColor")) s.SetColor("_SpecColor", new Color(0.6f, 0.6f, 0.62f));
            if (s.HasProperty("_Shininess")) s.SetFloat("_Shininess", 0.32f);
            if (s.HasProperty("_Cutoff")) s.SetFloat("_Cutoff", 0.01f);
            lk.wood = w; lk.steel = s;
            Debug.Log("[FirstPersonLoD] VR crates: boards and iron made in " + lk.jobMs + " ms (background), " + lk.colours + ", iron shader " + (sh != null ? sh.name : "none shiny: boards' shader"));
        }
        catch (Exception e) { lk.failed = true; Debug.Log("[FirstPersonLoD] VR crates: textures failed (" + e.GetType().Name + ": " + e.Message + "); crates stay the game's"); }
    }

    // unit cube (the game's crate is the default cube, scaled): submesh 0 boards (and the dark
    // inside), submesh 1 the iron angle bars
    private static Mesh CrateMesh()
    {
        if (crateMesh != null) return crateMesh;
        const float b = 0.125f, t = 0.022f, pt = 0.045f, g = 0.008f;
        List<Vector3> v = new List<Vector3>(); List<Vector2> uv = new List<Vector2>();
        List<int> t0 = new List<int>(), t1 = new List<int>();
        System.Random rnd = new System.Random(77);
        // iron: bars along each axis at the four edges; the x bars run the full length (they make
        // the corners), the others stop where they meet them
        for (int A = 0; A < 3; A++)
        {
            int B = (A + 1) % 3, C = (A + 2) % 3;
            for (int sb = -1; sb <= 1; sb += 2)
                for (int sc = -1; sc <= 1; sc += 2)
                {
                    Vector3 lo = Vector3.zero, hi = Vector3.zero;
                    if (A == 0) { lo[A] = -0.5f - t; hi[A] = 0.5f + t; } else { lo[A] = -0.5f + b; hi[A] = 0.5f - b; }
                    if (sb > 0) { lo[B] = 0.5f - b; hi[B] = 0.5f + t; } else { lo[B] = -0.5f - t; hi[B] = -0.5f + b; }
                    if (sc > 0) { lo[C] = 0.5f - b; hi[C] = 0.5f + t; } else { lo[C] = -0.5f - t; hi[C] = -0.5f + b; }
                    Box(v, uv, t1, lo, hi, A, A == 0, 2f * (b + t), -1, 0f, false, false);
                }
        }
        // boards: three a side, along y on the sides and along z on top and bottom
        float inner = 0.5f - b, bwid = 2f * inner / 3f;
        for (int N = 0; N < 3; N++)
            for (int s = -1; s <= 1; s += 2)
            {
                int Lax = N == 1 ? 2 : 1;
                int Cax = 3 - N - Lax;
                for (int p = 0; p < 3; p++)
                {
                    Vector3 lo = Vector3.zero, hi = Vector3.zero;
                    lo[Cax] = -inner + p * bwid + g * 0.5f; hi[Cax] = -inner + (p + 1) * bwid - g * 0.5f;
                    lo[Lax] = -inner; hi[Lax] = inner;
                    if (s > 0) { lo[N] = 0.5f - pt; hi[N] = 0.5f; } else { lo[N] = -0.5f; hi[N] = -0.5f + pt; }
                    int variant = rnd.Next(2);
                    Plank(v, uv, t0, lo, hi, N, s, Lax, Cax, variant, (float)rnd.NextDouble() * 0.19f, rnd.Next(2) == 1, rnd.Next(2) == 1);
                }
            }
        // the dark inside, seen through the gaps
        Vector3 c0 = new Vector3(-0.5f + pt + 0.002f, -0.5f + pt + 0.002f, -0.5f + pt + 0.002f);
        Box(v, uv, t0, c0, -c0, -1, true, 1f, -1, 0f, false, true);
        Mesh m = new Mesh(); m.name = "FPCrate";
        m.SetVertices(v); m.SetUVs(0, uv);
        m.subMeshCount = 2;
        m.SetTriangles(t0, 0); m.SetTriangles(t1, 1);
        m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
        crateMesh = m;
        return m;
    }

    private static void Quad(List<Vector3> v, List<Vector2> uv, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Vector3 facing)
    {
        int i = v.Count;
        v.Add(a); v.Add(b); v.Add(c); v.Add(d);
        uv.Add(ua); uv.Add(ub); uv.Add(uc); uv.Add(ud);
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), facing) >= 0f) { t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3); }
        else { t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i); t.Add(i + 3); t.Add(i + 2); }
    }

    // one face of an axis-aligned box: normal along axis n (sign s); corner k of the face in the
    // order (lo,lo) (hi,lo) (hi,hi) (lo,hi) over the two other axes p < q
    private static void Face(List<Vector3> v, List<Vector2> uv, List<int> t, Vector3 lo, Vector3 hi, int n, int s, UvMap map)
    {
        int p = n == 0 ? 1 : 0, q = n == 2 ? 1 : 2;
        Vector3[] c = new Vector3[4];
        for (int k = 0; k < 4; k++)
        {
            Vector3 x = Vector3.zero;
            x[n] = s > 0 ? hi[n] : lo[n];
            x[p] = (k == 1 || k == 2) ? hi[p] : lo[p];
            x[q] = (k >= 2) ? hi[q] : lo[q];
            c[k] = x;
        }
        Vector3 f = Vector3.zero; f[n] = s;
        Quad(v, uv, t, c[0], c[1], c[2], c[3], map(c[0]), map(c[1]), map(c[2]), map(c[3]), f);
    }

    // a box: iron bars (len = the bar's long axis, ends only when asked; texture across each face,
    // one tile per `tile` of length), or the dark inside (all faces to the dark band)
    private static void Box(List<Vector3> v, List<Vector2> uv, List<int> t, Vector3 lo, Vector3 hi, int len, bool ends, float tile, int unused, float unused2, bool unused3, bool dark)
    {
        for (int n = 0; n < 3; n++)
            for (int s = -1; s <= 1; s += 2)
            {
                if (dark) { Face(v, uv, t, lo, hi, n, s, delegate (Vector3 x) { return new Vector2(0.5f, 0.995f); }); continue; }
                if (n == len && !ends) continue;
                int across = n == len ? (len + 1) % 3 : 3 - n - len;
                int along = n == len ? (len + 2) % 3 : len;
                float a0 = lo[across], a1 = hi[across], l0 = n == len ? lo[along] : -0.5f - 0.022f;
                float aspan = Mathf.Max(1e-4f, a1 - a0);
                Face(v, uv, t, lo, hi, n, s, delegate (Vector3 x) { return new Vector2((x[across] - a0) / aspan, (x[along] - l0) / tile); });
            }
    }

    // a board: its outer face and its two long sides (seen in the gaps); texture: one of the two
    // boards of the texture, flipped and slid along at random
    private static void Plank(List<Vector3> v, List<Vector2> uv, List<int> t, Vector3 lo, Vector3 hi, int N, int s, int Lax, int Cax, int variant, float voff, bool flipU, bool flipV)
    {
        float c0 = lo[Cax], c1 = hi[Cax], l0 = lo[Lax], l1 = hi[Lax];
        float width = c1 - c0;
        float perUnit = 1f / (width * 4f);          // the texture is four board widths long
        float u0 = variant * 0.5f;
        UvMap outer = delegate (Vector3 x)
        {
            float fu = (x[Cax] - c0) / width; if (flipU) fu = 1f - fu;
            float fv = (x[Lax] - l0) * perUnit; if (flipV) fv = (l1 - x[Lax]) * perUnit;
            return new Vector2(u0 + 0.5f * Mathf.Clamp(fu, 0.004f, 0.996f), voff + fv);
        };
        Face(v, uv, t, lo, hi, N, s, outer);
        // the gaps read as dark grooves: the boards' long sides take the dark band
        UvMap side = delegate (Vector3 x) { return new Vector2(u0 + 0.25f, 0.995f); };
        Face(v, uv, t, lo, hi, Cax, -1, side);
        Face(v, uv, t, lo, hi, Cax, 1, side);
    }

    private static void Apply(Item it, bool on)
    {
        if (it.r == null) return;
        Look lk;
        looks.TryGetValue(it.tex != null ? it.tex.GetInstanceID() : 0, out lk);
        bool want = on && lk != null && lk.wood != null;
        if (want && it.fp == null)
        {
            GameObject g = new GameObject("FPCrate");
            g.layer = it.r.gameObject.layer;
            Transform gt = g.transform;
            gt.SetParent(it.r.transform, false);
            gt.localPosition = Vector3.zero; gt.localRotation = Quaternion.identity; gt.localScale = Vector3.one;
            g.AddComponent<MeshFilter>().sharedMesh = CrateMesh();
            MeshRenderer fr = g.AddComponent<MeshRenderer>();
            fr.sharedMaterials = new Material[] { lk.wood, lk.steel };
            fr.shadowCastingMode = it.r.shadowCastingMode;
            fr.receiveShadows = it.r.receiveShadows;
            it.fp = g; it.fr = fr; it.colorSet = false;
        }
        if (it.fp != null && it.fp.activeSelf != want) it.fp.SetActive(want);
        if (want && !it.hidden) { it.r.enabled = false; it.hidden = true; }
        else if (!want && it.hidden) { it.r.enabled = true; it.hidden = false; }
    }

    public static void Show(bool on)
    {
        if (shown == on) return;
        shown = on;
        for (int i = items.Count - 1; i >= 0; i--) { if (items[i].r == null) { items.RemoveAt(i); continue; } Apply(items[i], on); }
    }

    public static void Tick(GameObject room)
    {
        if (!FPConfig.Crates3D) return;
        foreach (KeyValuePair<int, Look> kv in looks)
        {
            Look lk = kv.Value;
            if (lk.job == null || !lk.job.done) continue;
            if (lk.job.error != null) { Debug.Log("[FirstPersonLoD] VR crates: making the textures failed: " + lk.job.error); lk.failed = true; lk.job = null; continue; }
            lk.jobMs = lk.job.ms;
            Finish(lk);
            for (int i = 0; i < items.Count; i++) if (items[i].r != null) Apply(items[i], shown);
            break;   // one upload per frame
        }
        if (!shown) return;
        if (room != null && Time.realtimeSinceStartup >= nextScan) { nextScan = Time.realtimeSinceStartup + 1.5f; Room(room); }
        for (int i = items.Count - 1; i >= 0; i--)
        {
            Item it = items[i];
            if (it.r == null) { items.RemoveAt(i); continue; }
            if (!it.hidden || it.fr == null) continue;
            if (it.r.enabled) it.r.enabled = false;      // something switched the game's crate back on
            Material m = it.r.sharedMaterial;
            if (m != null && m.HasProperty("_Color"))
            {
                Color c = m.color;
                if (!it.colorSet || c != it.lastColor)
                {
                    it.lastColor = c; it.colorSet = true;
                    mpb.Clear(); mpb.SetColor("_Color", c); it.fr.SetPropertyBlock(mpb);
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Doorways (first person only). A door in the back wall is a black box (DoorBlack) two units deep
// with invisible walls, roof, floor and back (colliders only) and a light inside that the game
// switches on when the door can be used. First person builds the passage: bricks inside each of
// those invisible pieces (like the Tavern walls, HD and rough through BrickBatch), and the black
// box keeps only its far part, so you look into a short stone passage that fades to black, lit
// by the door's own light when the game lights it. The door's renderer keeps working as the
// game's (its fades and colour); only its mesh is swapped. Tabletop gets the game's door.
// ---------------------------------------------------------------------------------------------
public static class Doorways
{
    private sealed class Door { public MeshFilter mf; public Mesh src; public Mesh far; public readonly List<GameObject> added = new List<GameObject>(); }
    private static readonly List<Door> doors = new List<Door>();
    private static readonly Dictionary<int, bool> done = new Dictionary<int, bool>();
    private static readonly Dictionary<string, Mesh> farBoxes = new Dictionary<string, Mesh>();
    private static bool shown = true;
    private static int logged, sizeLogs;

    // the door's own box (its mesh bounds), keeping only the far part along one local axis: the
    // part of the depth given by DoorBlackDepth, at the end that points away from the room
    private static Mesh FarBox(Bounds sb, int axis, int sign)
    {
        float keep = Mathf.Clamp(FPConfig.DoorBlackDepth, 0.05f, 1f);
        string key = axis + "|" + sign + "|" + keep.ToString("0.000") + "|" + sb.min.ToString("F3") + sb.max.ToString("F3");
        Mesh fb;
        if (farBoxes.TryGetValue(key, out fb) && fb != null) return fb;
        Vector3 lo = sb.min, hi = sb.max;
        float len = hi[axis] - lo[axis];
        if (sign > 0) lo[axis] = hi[axis] - len * keep; else hi[axis] = lo[axis] + len * keep;
        Vector3[] c = {
            new Vector3(lo.x,lo.y,lo.z), new Vector3(hi.x,lo.y,lo.z), new Vector3(hi.x,hi.y,lo.z), new Vector3(lo.x,hi.y,lo.z),
            new Vector3(lo.x,lo.y,hi.z), new Vector3(hi.x,lo.y,hi.z), new Vector3(hi.x,hi.y,hi.z), new Vector3(lo.x,hi.y,hi.z) };
        // outward faces, clockwise seen from outside (Unity's front face)
        int[][] faces = { new[] {0,3,2,1}, new[] {4,5,6,7}, new[] {0,1,5,4}, new[] {3,7,6,2}, new[] {0,4,7,3}, new[] {1,2,6,5} };
        List<Vector3> v = new List<Vector3>(); List<int> t = new List<int>(); List<Vector2> uv = new List<Vector2>();
        for (int f = 0; f < faces.Length; f++)
        {
            int b = v.Count;
            for (int k = 0; k < 4; k++) v.Add(c[faces[f][k]]);
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(0, 1)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(1, 0));
            t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b); t.Add(b + 2); t.Add(b + 3);
        }
        fb = new Mesh(); fb.name = "FPDoorFarBox";
        fb.SetVertices(v); fb.SetUVs(0, uv); fb.SetTriangles(t, 0); fb.RecalculateNormals(); fb.RecalculateBounds();
        farBoxes[key] = fb;
        return fb;
    }

    // which local axis of the door runs into the wall, and which way: toward its "doorback" piece
    // when it has one, else away from the middle of the room
    private static void Depth(Transform door, Bounds sb, Vector3 roomMid, out int axis, out int sign, out string how)
    {
        Transform back = null;
        for (int c = 0; c < door.childCount; c++) if (door.GetChild(c).name.StartsWith("doorback")) { back = door.GetChild(c); break; }
        Vector3 d;
        if (back != null)
        {
            Collider bc = back.GetComponent<Collider>();
            d = door.InverseTransformPoint(bc != null ? bc.bounds.center : back.position) - sb.center;
            how = "toward doorback";
        }
        else { d = door.InverseTransformPoint(roomMid) - sb.center; d = -d; how = "away from the room's middle"; }
        // scale by the box size so a long thin door still picks its real depth axis
        Vector3 sz = sb.size;
        Vector3 n = new Vector3(d.x / Mathf.Max(0.001f, sz.x), d.y / Mathf.Max(0.001f, sz.y), d.z / Mathf.Max(0.001f, sz.z));
        axis = Mathf.Abs(n.x) >= Mathf.Abs(n.y) && Mathf.Abs(n.x) >= Mathf.Abs(n.z) ? 0 : (Mathf.Abs(n.y) >= Mathf.Abs(n.z) ? 1 : 2);
        if (back == null) axis = 2;
        sign = d[axis] >= 0f ? 1 : -1;
    }

    public static void Room(GameObject room)
    {
        if (!FPConfig.DoorTunnels || room == null) return;
        if (!room.activeInHierarchy) return;   // colliders read as empty until the room is on
        int rid = room.GetInstanceID();
        if (done.ContainsKey(rid)) return;
        try
        {
            Doors[] ds = room.GetComponentsInChildren<Doors>(true);
            if (ds.Length == 0) { done[rid] = true; return; }
            // brick mesh and materials: the room's own
            Mesh mesh = null; List<Material> mats = new List<Material>(); List<int> weights = new List<int>(); int total = 0;
            MeshRenderer template = null;
            Dictionary<Material, int> mc = new Dictionary<Material, int>();
            MeshRenderer[] rs = room.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i].name != "SmallBlock") continue;
                MeshFilter f = rs[i].GetComponent<MeshFilter>();
                Material m = rs[i].sharedMaterial;
                if (f == null || f.sharedMesh == null || m == null || m.mainTexture == null || !m.mainTexture.name.StartsWith("Block")) continue;
                if (mesh == null) { mesh = f.sharedMesh; template = rs[i]; }
                int n; mc.TryGetValue(m, out n); mc[m] = n + 1;
            }
            if (mesh == null) return;   // room not switched on yet: try again on the next pass
            done[rid] = true;
            foreach (KeyValuePair<Material, int> kv in mc) { mats.Add(kv.Key); weights.Add(kv.Value); total += kv.Value; }
            Bounds roomB = new Bounds(); bool haveRoom = false;
            for (int i = 0; i < rs.Length; i++) if (rs[i].name == "SmallBlock") { if (!haveRoom) { roomB = rs[i].bounds; haveRoom = true; } else roomB.Encapsulate(rs[i].bounds); }
            int made = 0, passages = 0, coveredAll = 0;
            StringBuilder sizes = new StringBuilder();
            for (int d = 0; d < ds.Length; d++)
            {
                Doors door = ds[d];
                if (door == null) continue;
                MeshFilter dmf = door.GetComponent<MeshFilter>();
                MeshRenderer dmr = door.GetComponent<MeshRenderer>();
                if (dmf == null || dmr == null || dmf.sharedMesh == null)
                {
                    if (sizeLogs < 16) { sizeLogs++; sizes.Append("\n  " + door.name + ": no mesh of its own (" + (dmf == null ? "no MeshFilter" : dmr == null ? "no MeshRenderer" : "empty") + "), left as the game's"); }
                    continue;
                }
                Door rec = new Door(); rec.mf = dmf; rec.src = dmf.sharedMesh;
                Bounds sb = rec.src.bounds;
                int axis, sign; string how;
                Depth(door.transform, sb, roomB.center, out axis, out sign, out how);
                rec.far = FarBox(sb, axis, sign);
                // what is visible near the door already (the wall around the opening), not the door itself
                List<Bounds> obs = new List<Bounds>();
                Bounds near = dmr.bounds; near.Expand(3f);
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] == null || rs[i] == dmr || rs[i].transform.IsChildOf(door.transform) || rs[i].name == "FPBrick") continue;
                    if (rs[i].bounds.Intersects(near)) obs.Add(rs[i].bounds);
                }
                int before = made;
                for (int c = 0; c < door.transform.childCount; c++)
                {
                    Transform ch = door.transform.GetChild(c);
                    // doorroof, doorwall(s), doorfloor; not doorback (it sits behind the black)
                    if (!ch.name.StartsWith("door") || ch.name.StartsWith("doorback")) continue;
                    BoxCollider bc = ch.GetComponent<BoxCollider>();
                    if (bc == null || bc.isTrigger) continue;
                    int cov;
                    int got = RoomShape.FillBox(bc.bounds, room.transform, mesh, template, mats, weights, total, obs, rec.added, out cov);
                    coveredAll += cov;
                    if (sizeLogs < 16) { sizeLogs++; sizes.Append("\n  " + door.name + "/" + ch.name + " " + bc.bounds.size.ToString("F2") + ": " + (got < 0 ? "not a wall (skipped)" : got + " bricks, " + cov + " spots already covered")); }
                    if (got > 0) made += got;
                }
                if (sizeLogs < 16) { sizeLogs++; sizes.Append("\n  " + door.name + " black box " + sb.size.ToString("F2") + " (local), scale " + door.transform.lossyScale.ToString("F2") + ", depth along " + "xyz"[axis] + (sign > 0 ? "+" : "-") + " (" + how + ")"); }
                if (made > before) passages++;
                doors.Add(rec);
                Apply(rec, shown);
            }
            if (logged++ < 20) Debug.Log("[FirstPersonLoD] VR doorways: " + room.name + ": " + passages + " of " + ds.Length + " doors given a stone passage (" + made + " bricks, " + coveredAll + " spots already covered); the black keeps the far "
                + (FPConfig.DoorBlackDepth * 100f).ToString("0") + "% of the door's depth" + sizes);
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] VR doorways: " + room.name + " failed: " + e.GetType().Name + ": " + e.Message); }
    }

    private static void Apply(Door d, bool on)
    {
        if (d.mf != null) d.mf.sharedMesh = on && d.far != null ? d.far : d.src;
        for (int i = 0; i < d.added.Count; i++) if (d.added[i] != null && d.added[i].activeSelf != on) d.added[i].SetActive(on);
    }

    public static void Show(bool on)
    {
        if (shown == on) return;
        shown = on;
        for (int i = doors.Count - 1; i >= 0; i--) { if (doors[i].mf == null) { doors.RemoveAt(i); continue; } Apply(doors[i], on); }
    }
}

// ---------------------------------------------------------------------------------------------
// Where a frame's time goes (main thread), measured every frame from the mod's own Update,
// FixedUpdate and LateUpdate plus the cameras' cull/render callbacks:
//   physics   first FixedUpdate -> Update       (FixedUpdates and early Updates)
//   scripts   Update -> LateUpdate              (the game's Update scripts)
//   late      LateUpdate -> first camera cull   (LateUpdates, animation)
//   render    first cull -> last camera render  (culling + draw submission, both eyes, all cameras)
//   wait      last render -> next frame         (present, waiting on the headset, GPU sync)
// A frame that is CPU-bound shows large physics/scripts/render; a GPU- or headset-bound frame shows
// a large wait. Also: camera renders per frame (2 head renders = multi-pass stereo, every object
// drawn twice), and garbage collections (each one freezes the game).
// PerfProbe: once per floor, 10 s after arriving, cycles the render setups below for 4 s each and
// logs a comparison table, so one run shows which setting costs what on this machine.
// ---------------------------------------------------------------------------------------------
public static class FramePerf
{
    private static readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
    private static double tUpdate = -1, tFixed = -1, tLate = -1, tCull = -1, tPost = -1;
    private static bool installed;
    public static Camera HeadCam;
    private static int headRenders, allRenders;
    private static double camStart = -1;
    private static int stallLogs, gcLast;
    private static readonly Dictionary<string, double> camMs = new Dictionary<string, double>();
    private static readonly Dictionary<int, string> camNames = new Dictionary<int, string>();
    // accumulators (reset by Report)
    private static double sPhys, sScripts, sLate, sRender, sWait;
    private static int n, headRenderSum, allRenderSum, gcAtReport = -1;
    // probe accumulators
    private static double pRender, pTotal, pWorst;
    private static int pFrames;

    private static double Now() { return sw.Elapsed.TotalMilliseconds; }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Camera.onPreCull += delegate (Camera c) { double t = Now(); if (tCull < 0) tCull = t; camStart = t; };
        Camera.onPostRender += delegate (Camera c)
        {
            double t = Now();
            tPost = t; allRenders++; if (c == HeadCam) headRenders++;
            string k;
            if (c == HeadCam) k = "head (both eyes)";
            else if (!camNames.TryGetValue(c.GetInstanceID(), out k)) { k = c.name; camNames[c.GetInstanceID()] = k; }
            double v; camMs.TryGetValue(k, out v); camMs[k] = v + (camStart >= 0 ? t - camStart : 0);
            camStart = -1;
        };
    }

    private static double prevTotal;
    public static void MarkFixed() { if (tFixed < 0) tFixed = Now(); }

    public static void MarkUpdate()
    {
        double t = Now();
        // close the previous frame
        if (tUpdate >= 0 && tLate >= 0)
        {
            double start = tFixed >= 0 ? tFixed : t;
            double phys = t - start;
            double scripts = tLate - tUpdate;
            double renderStart = tCull >= tLate ? tCull : tLate;
            double renderEnd = tPost >= renderStart ? tPost : renderStart;
            double late = renderStart - tLate;
            double render = renderEnd - renderStart;
            double wait = start - renderEnd;
            if (wait < 0) wait = 0;
            sPhys += phys; sScripts += scripts; sLate += late; sRender += render; sWait += wait; n++;
            headRenderSum += headRenders; allRenderSum += allRenders;
            double total = t - tUpdate;
            pRender += render; pTotal += total; pFrames++; if (total > pWorst) pWorst = total;
            // a freeze: say which part of the frame took the time (and whether a garbage collection ran)
            int gcNow = System.GC.CollectionCount(0);
            if (total > FPConfig.StallMs && stallLogs < 60)
            {
                stallLogs++;
                Debug.Log("[FirstPersonLoD] VR stall: frame took " + total.ToString("0") + " ms: physics+early updates " + phys.ToString("0.0") + ", scripts " + scripts.ToString("0.0")
                    + ", late+pose wait " + late.ToString("0.0") + ", render " + render.ToString("0.0") + ", after render " + wait.ToString("0.0")
                    + (gcNow != gcLast ? ", GARBAGE COLLECTION ran" : "") + (Time.timeScale == 0f ? ", game paused" : "")
#if BETA
                    + "; the mod's own work " + VRFP.LastTickMs.ToString("0.0") + " ms"
#endif
                    + "; frame before took " + prevTotal.ToString("0") + " ms");
            }
            gcLast = gcNow;
            prevTotal = total;
        }
        tUpdate = t; tFixed = -1; tLate = -1; tCull = -1; headRenders = 0; allRenders = 0;
    }

    public static void MarkLate() { tLate = Now(); }

    public static string Report()
    {
        if (n == 0) return "frame split: no data";
        int gc = System.GC.CollectionCount(0);
        string r = "frame split (main thread, ms): physics " + (sPhys / n).ToString("0.0") + ", scripts " + (sScripts / n).ToString("0.0")
            + ", late+pose wait " + (sLate / n).ToString("0.0") + ", render " + (sRender / n).ToString("0.0") + ", wait " + (sWait / n).ToString("0.0")
            + "; head camera renders " + ((float)headRenderSum / n).ToString("0.0") + "/frame" + (headRenderSum >= 2 * n - 1 ? " (multi-pass stereo: everything drawn once per eye)" : "")
            + ", all camera renders " + ((float)allRenderSum / n).ToString("0.0") + "/frame" + CamSplit(n)
            + (gcAtReport >= 0 ? ", garbage collections " + (gc - gcAtReport) : "")
            + ", light mode/shadow changes " + VRPerf.TakeChurn();
        gcAtReport = gc;
        sPhys = sScripts = sLate = sRender = sWait = 0; n = 0; headRenderSum = allRenderSum = 0;
        return r;
    }

    private static string CamSplit(int frames)
    {
        if (camMs.Count == 0 || frames == 0) return "";
        StringBuilder sb = new StringBuilder(" [");
        bool first = true;
        foreach (KeyValuePair<string, double> kv in camMs) { sb.Append((first ? "" : ", ") + kv.Key + " " + (kv.Value / frames).ToString("0.00") + " ms"); first = false; }
        camMs.Clear();
        return sb.Append("]").ToString();
    }

    // ---- probe ----
    private struct Mode { public string name, path; public int cap, near, shadows; }
    private static readonly Mode[] modes = new Mode[]
    {
        new Mode { name = "current settings",              path = null,      cap = -2, near = -2, shadows = -2 },
        new Mode { name = "forward, + 1 shadowed light",   path = "forward", cap = -2, near = -2, shadows = 1 },
        new Mode { name = "forward, 3 lights",             path = "forward", cap = 3,  near = 2,  shadows = 0 },
        new Mode { name = "game deferred, no shadows",     path = "game",    cap = -2, near = 0,  shadows = 0 },
        new Mode { name = "game deferred, 1 shadow",       path = "game",    cap = -2, near = 0,  shadows = 1 },
    };
    private static int probeMode = -1;
    private static float probeAt = -1f, modeStart, collectFrom;
    private static string savedPath; private static int savedCap, savedNear, savedShadows;
    private static StringBuilder table;
    private static float gpuSum; private static int gpuN;
    public static bool Running { get { return probeMode >= 0; } }

    public static void ArmProbe(string why) { if (FPConfig.PerfProbe && probeMode < 0) { probeAt = Time.realtimeSinceStartup + 10f; Debug.Log("[FirstPersonLoD] VR perf probe: will compare render setups in 10 s (" + why + "); standing still gives the cleanest numbers"); } }

    public static void GpuSample(float ms) { if (probeMode >= 0 && Time.realtimeSinceStartup >= collectFrom) { gpuSum += ms; gpuN++; } }

    private static void Apply(Mode m)
    {
        FPConfig.FPRenderPath = m.path ?? savedPath;
        FPConfig.FPPixelLights = m.cap == -2 ? savedCap : m.cap;
        FPConfig.FPNearLights = m.near == -2 ? savedNear : m.near;
        FPConfig.FPShadowLights = m.shadows == -2 ? savedShadows : m.shadows;
        VRPerf.Restore(); // re-applied with these values on the next tick
    }

    public static void ProbeTick(bool driving)
    {
        float t = Time.realtimeSinceStartup;
        if (probeMode < 0)
        {
            if (probeAt < 0f || t < probeAt || !driving) return;
            probeAt = -1f;
            savedPath = FPConfig.FPRenderPath; savedCap = FPConfig.FPPixelLights; savedNear = FPConfig.FPNearLights; savedShadows = FPConfig.FPShadowLights;
            table = new StringBuilder("[FirstPersonLoD] VR perf probe results (same spot, 4 s each, first second discarded):");
            probeMode = 0; Begin(t);
            return;
        }
        if (!driving) { Abort("first person stopped"); return; }
        if (t - modeStart < 4f) return;
        Mode m = modes[probeMode];
        float fps = pTotal > 0 ? (float)(pFrames * 1000.0 / pTotal) : 0f;
        table.Append("\n  " + m.name.PadRight(32) + " " + fps.ToString("0").PadLeft(3) + " fps, frame " + (pFrames > 0 ? (pTotal / pFrames).ToString("0.0") : "?")
            + " ms (worst " + pWorst.ToString("0") + "), render CPU " + (pFrames > 0 ? (pRender / pFrames).ToString("0.0") : "?") + " ms, GPU "
            + (gpuN > 0 ? (gpuSum / gpuN).ToString("0.0") : "?") + " ms");
        probeMode++;
        if (probeMode >= modes.Length) { Finish(); return; }
        Begin(t);
    }

    private static void Begin(float t)
    {
        Apply(modes[probeMode]);
        modeStart = t; collectFrom = t + 1f;
        pRender = pTotal = pWorst = 0; pFrames = 0; gpuSum = 0; gpuN = 0;
    }

    public static void ResetCollect()
    {
        // called every frame: discard the first second of each mode (the switch itself hitches)
        if (probeMode >= 0 && Time.realtimeSinceStartup < collectFrom) { pRender = pTotal = pWorst = 0; pFrames = 0; }
    }

    private static void Finish()
    {
        probeMode = -1;
        FPConfig.FPRenderPath = savedPath; FPConfig.FPPixelLights = savedCap; FPConfig.FPNearLights = savedNear; FPConfig.FPShadowLights = savedShadows;
        VRPerf.Restore();
        Debug.Log(table.ToString() + "\n  (your settings are back)");
    }

    public static void Abort(string why)
    {
        if (probeMode < 0) return;
        probeMode = -1;
        FPConfig.FPRenderPath = savedPath; FPConfig.FPPixelLights = savedCap; FPConfig.FPNearLights = savedNear; FPConfig.FPShadowLights = savedShadows;
        VRPerf.Restore();
        Debug.Log(table.ToString() + "\n  probe stopped early: " + why + " (your settings are back)");
    }
}

// ---------------------------------------------------------------------------------------------
// First-person render budget and lighting.
// The game renders with DEFERRED shading and ~30-35 point lights per floor. In deferred every light
// is a screen-space pass over the pixels inside its radius, per eye. Seen from the tabletop view the
// radii cover a small part of the screen; standing inside the room nearly every light covers the
// whole view, so the GPU shades the screen ~30 times per eye. The pixel-light cap does nothing in
// deferred. While first person drives:
//   - the headset camera renders FORWARD, where pixelLightCount caps the per-pixel lights per
//     object (the rest fall back to cheap vertex/SH lighting)
//   - shadows are kept only on the nearest casters, shadow distance shortened
//   - a warm head light (like carrying a torch) and an ambient lift keep the dungeon readable
//     at eye level, where the game's lights (placed for the side view) mostly miss what you face
// Everything is restored when the rig is handed back. F3 toggles the render path live.
// ---------------------------------------------------------------------------------------------
// All lights in the scene without FindObjectsOfType (which walks every object and cost ~25 ms each
// time the light pass ran, once a second): Light.GetLights reads the engine's own list of active
// lights, per type and layer.
public static class SceneLights
{
    private static readonly List<Light> buf = new List<Light>();
    private static readonly Dictionary<int, bool> seen = new Dictionary<int, bool>();
    private static readonly LightType[] types = { LightType.Point, LightType.Spot, LightType.Directional };
    // GetLights(type, layer) returns every light of that type that shines on that layer, so a light
    // shining on all layers comes back once per layer: keep each light once
    public static Light[] All()
    {
        buf.Clear(); seen.Clear();
        for (int t = 0; t < types.Length; t++)
            for (int layer = 0; layer < 32; layer++)
            {
                Light[] ls = Light.GetLights(types[t], layer);
                if (ls == null) continue;
                for (int i = 0; i < ls.Length; i++)
                {
                    if (ls[i] == null) continue;
                    int id = ls[i].GetInstanceID();
                    if (seen.ContainsKey(id)) continue;
                    seen[id] = true; buf.Add(ls[i]);
                }
            }
        return buf.ToArray();
    }
}

public static class VRPerf
{
    private static bool applied;
    private static int savedPixelLights;
    private static float savedShadowDistance;
    private static Camera cam;
    private static RenderingPath savedPath;
    private static Color savedAmbient, savedEquator, savedGround;
    private static float savedAmbientIntensity;
    private static GameObject headLightGo;
    private static Light headLight;
    private static readonly Dictionary<Light, LightShadows> savedShadows = new Dictionary<Light, LightShadows>();
    private static readonly Dictionary<Light, LightRenderMode> savedModes = new Dictionary<Light, LightRenderMode>();
    private static readonly Dictionary<Light, int> savedRes = new Dictionary<Light, int>();
    public static int TakeChurn() { int c = churn; churn = 0; return c; }
    private static float nextLightPass;
    private static readonly List<Light> casters = new List<Light>();
    private static readonly List<Light> near = new List<Light>();
    private static Vector3 sortFrom;
    private static bool savedFog, savedOcclusion;
    private static readonly List<Camera> offCams = new List<Camera>();
    private static readonly List<Camera> throttled = new List<Camera>();

    public static void TickThrottled()
    {
        if (throttled.Count == 0 || Time.frameCount % Mathf.Max(1, FPConfig.TextureCameraEvery) != 0) return;
        for (int i = 0; i < throttled.Count; i++) if (throttled[i] != null) throttled[i].Render();
    }

    // A camera that renders into the headset BEFORE the head camera, which then clears the whole
    // view, draws nothing you can see: its culling and draw calls are pure cost. The game has one
    // ("_Stats", depth -1, layer 8 only), switched off while first person drives.
    private static void HideOverdrawnCameras(Camera head)
    {
        if (!FPConfig.SkipHiddenCameras) return;
        bool headClears = head.clearFlags == CameraClearFlags.SolidColor || head.clearFlags == CameraClearFlags.Skybox;
        Camera[] all = Camera.allCameras;
        for (int i = 0; i < all.Length; i++)
        {
            Camera c = all[i];
            if (c == null || c == head || !c.enabled || c.name.StartsWith("FP")) continue;
            string what = "'" + c.name + "' (depth " + c.depth + ", mask 0x" + c.cullingMask.ToString("X") + ", eye " + c.stereoTargetEye
                + ", " + (c.targetTexture != null ? "renders into texture " + c.targetTexture.name + " " + c.targetTexture.width + "x" + c.targetTexture.height : "renders to the headset") + ")";
            if (c.targetTexture != null && FPConfig.TextureCameraEvery > 1)
            {
                // an in-world screen (the game's "_Stats" board): refresh it a few times a second
                c.enabled = false;
                offCams.Add(c); throttled.Add(c);
                Debug.Log("[FirstPersonLoD] VR: camera " + what + " refreshed every " + FPConfig.TextureCameraEvery + " frames instead of every frame while first person drives");
            }
            else if (c.targetTexture == null && headClears && head.targetTexture == null && c.depth < head.depth && c.stereoTargetEye != StereoTargetEyeMask.None)
            {
                c.enabled = false;
                offCams.Add(c);
                Debug.Log("[FirstPersonLoD] VR: camera " + what + " draws before the head camera clears the view, so nothing of it is visible; off while first person drives");
            }
            else Debug.Log("[FirstPersonLoD] VR: camera " + what + " left as is");
        }
    }
    private static float lastFogStart = float.NaN;
    public static string Note = "";

    private static int ByDistance(Light a, Light b)
    {
        return (a.transform.position - sortFrom).sqrMagnitude.CompareTo((b.transform.position - sortFrom).sqrMagnitude);
    }

    // how far outside its reach a light is from the player (negative = the player is inside it)
    private static float Reach(Light l)
    {
        return (l.transform.position - sortFrom).magnitude - l.range;
    }

    // ranking with hysteresis: a light already chosen keeps a head start, so walking around does not
    // flip lights between modes every second (each new combination can compile a shader variant on
    // first use, a likely source of the long hitches)
    private static readonly Dictionary<Light, float> score = new Dictionary<Light, float>();
    private static int ByScore(Light a, Light b) { return score[a].CompareTo(score[b]); }
    private static int churn;

    private static Color Floor(Color c, float m)
    {
        return new Color(Mathf.Max(c.r, m), Mathf.Max(c.g, m), Mathf.Max(c.b, m), c.a);
    }

    // one line with everything the view toggle changes and hands back, to compare across toggles
    public static string State(Camera c)
    {
        return "camera " + (c != null ? c.name + " path " + c.renderingPath + "/" + c.actualRenderingPath + " mask 0x" + c.cullingMask.ToString("X")
                + " near " + c.nearClipPlane.ToString("0.###") + " far " + c.farClipPlane.ToString("0.#") : "none")
            + "; pixel lights " + QualitySettings.pixelLightCount + ", shadow distance " + QualitySettings.shadowDistance.ToString("0.#")
            + "; fog " + RenderSettings.fog + " start " + RenderSettings.fogStartDistance.ToString("0.#") + " end " + RenderSettings.fogEndDistance.ToString("0.#")
            + "; ambient " + RenderSettings.ambientMode + " " + RenderSettings.ambientLight + " x" + RenderSettings.ambientIntensity.ToString("0.##")
            + "; head light " + (headLight != null && headLight.enabled ? "on" : "off")
            + "; lights held: " + savedShadows.Count + " shadows, " + savedModes.Count + " render modes"
            + (applied ? "; first-person budget APPLIED" : "; first-person budget not applied");
    }

    // Tavern showcase: in the Tavern (a small room) first person can afford more: a higher per-pixel
    // light cap and the nearest shadow-casting light keeps its shadows (the new walls and ceiling
    // then catch real shadows). Watched: if frames average over 12 ms for 3 s there, it goes back
    // to the normal budget for the rest of the session and says so.
    private static bool showcaseOff, showcaseLogged;
    private static float showcaseDt, showcaseSince = -1f; private static int showcaseFrames, showcaseSlow;
    public static bool InShowcase
    {
        get { return FPConfig.TavernShowcase && !showcaseOff && VRFP.CurrentRoomName == "Tavern"; }
    }
    private static void ShowcaseWatch()
    {
        if (!InShowcase) { showcaseDt = 0f; showcaseFrames = 0; showcaseSlow = 0; showcaseSince = -1f; return; }
        if (showcaseSince < 0f) showcaseSince = Time.realtimeSinceStartup;
        if (!showcaseLogged)
        {
            showcaseLogged = true;
            Debug.Log("[FirstPersonLoD] VR Tavern showcase lighting: up to " + FPConfig.TavernPixelLights + " per-pixel lights and the nearest light's shadows while you are in the Tavern");
        }
        // the first 10 s in the Tavern are loading (HD textures, room preparation): not judged
        if (Time.realtimeSinceStartup - showcaseSince < 10f) return;
        float dt = Time.unscaledDeltaTime;
        showcaseDt += dt; showcaseFrames++;
        if (dt > 0.0125f) showcaseSlow++;
        if (showcaseDt < 4f) return;
        float slow = showcaseSlow / (float)Mathf.Max(1, showcaseFrames);
        float avg = showcaseDt / Mathf.Max(1, showcaseFrames) * 1000f;
        showcaseDt = 0f; showcaseFrames = 0; showcaseSlow = 0;
        if (slow > 0.3f)
        {
            showcaseOff = true;
            nextLightPass = 0f;
            Debug.Log("[FirstPersonLoD] VR Tavern showcase lighting: " + (slow * 100f).ToString("0") + "% of frames over 12.5 ms (average " + avg.ToString("0.0")
                + " ms) over 4 s; back to the normal lighting budget for this session");
        }
    }

    public static void Tick(Vector3 playerPos, Camera headCam, Transform head, float worldScale)
    {
        VisualsTick();
        ShowcaseWatch();
        if (!applied)
        {
            applied = true;
            cam = headCam;
            savedPixelLights = QualitySettings.pixelLightCount;
            savedShadowDistance = QualitySettings.shadowDistance;
            ApplyVisuals(headCam);
            if (FPConfig.FPPixelLights >= 0) QualitySettings.pixelLightCount = FPConfig.FPPixelLights;
            if (FPConfig.FPShadowDistance > 0f) QualitySettings.shadowDistance = FPConfig.FPShadowDistance;
            string pathNote = "?";
            if (cam != null)
            {
                savedPath = cam.renderingPath;
                // procedurally built floors have no baked occlusion data: skip the occlusion query
                savedOcclusion = cam.useOcclusionCulling;
                cam.useOcclusionCulling = false;
                HideOverdrawnCameras(cam);
                RenderingPath before = cam.actualRenderingPath;
                if (FPConfig.FPRenderPath == "forward") cam.renderingPath = RenderingPath.Forward;
                pathNote = before + " -> " + cam.actualRenderingPath;
            }
            savedAmbient = RenderSettings.ambientLight;
            savedEquator = RenderSettings.ambientEquatorColor;
            savedGround = RenderSettings.ambientGroundColor;
            savedAmbientIntensity = RenderSettings.ambientIntensity;
            float mul = FPConfig.FPAmbient > 0f ? FPConfig.FPAmbient : 1f;
            float min = Mathf.Clamp01(FPConfig.FPAmbientMin);
            if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox)
                RenderSettings.ambientIntensity = savedAmbientIntensity * mul;
            else
            {
                // the game's ambient is black, so a multiplier alone does nothing: a small floor
                // keeps unlit corners from going pitch black at eye level
                RenderSettings.ambientLight = Floor(savedAmbient * mul, min);
                RenderSettings.ambientEquatorColor = Floor(savedEquator * mul, min);
                RenderSettings.ambientGroundColor = Floor(savedGround * mul, min);
            }
            // The game's VR fog (black, from 18 units; -200 = everything black while it loads) is
            // tuned for the tabletop camera. First person renders forward, where fog reaches every
            // surface, so a fog start left at -200 turns the whole view black. Off by default; the
            // loading darkness comes through the game's black drop instead (VRFP.GameFadeTick).
            savedFog = RenderSettings.fog;
            if (!FPConfig.FPFog) RenderSettings.fog = false;
            lastFogStart = RenderSettings.fogStartDistance;
            nextLightPass = 0f;
            Debug.Log("[FirstPersonLoD] VR render budget: path " + pathNote + ", pixel lights " + savedPixelLights + " -> " + QualitySettings.pixelLightCount
                + ", shadow distance " + savedShadowDistance.ToString("0.#") + " -> " + QualitySettings.shadowDistance.ToString("0.#")
                + ", shadowed lights kept: " + (FPConfig.FPShadowLights < 0 ? "all" : (cam != null && cam.actualRenderingPath == RenderingPath.DeferredShading ? Mathf.Max(FPConfig.FPDeferredShadows, FPConfig.FPShadowLights) : FPConfig.FPShadowLights).ToString())
                + "\n  ambient mode " + RenderSettings.ambientMode + " color " + savedAmbient + " x" + FPConfig.FPAmbient.ToString("0.##")
                + " floor " + FPConfig.FPAmbientMin.ToString("0.###") + " -> " + RenderSettings.ambientLight
                + ", intensity " + savedAmbientIntensity.ToString("0.##") + "; nearest " + FPConfig.FPNearLights + " lights always per-pixel"
                + "; fog " + savedFog + (savedFog && !RenderSettings.fog ? " (off in first person)" : "") + " " + RenderSettings.fogMode
                + " start " + RenderSettings.fogStartDistance.ToString("0.#") + " end " + RenderSettings.fogEndDistance.ToString("0.#")
                + " density " + RenderSettings.fogDensity.ToString("0.###") + " color " + RenderSettings.fogColor);
        }

        // head light (a carried torch)
        if (FPConfig.FPHeadLight > 0f && head != null)
        {
            if (headLightGo == null)
            {
                headLightGo = new GameObject("FPHeadLight");
                UnityEngine.Object.DontDestroyOnLoad(headLightGo);
                headLight = headLightGo.AddComponent<Light>();
                headLight.type = LightType.Point;
                headLight.shadows = LightShadows.None;
                headLight.renderMode = LightRenderMode.ForcePixel;
                headLight.color = new Color(1f, 0.82f, 0.62f);
            }
            headLight.enabled = true;
            headLight.intensity = FPConfig.FPHeadLight;
            headLight.range = Mathf.Max(FPConfig.HeadLightMeters * worldScale, 0.1f);
            headLightGo.transform.position = head.position + Vector3.up * (0.15f * worldScale);
        }
        else if (headLight != null) headLight.enabled = false;

        // fog: the game writes its start distance on every fade; keep first person's setting and
        // log what the game asked for (evidence for any darkness report)
        if (!FPConfig.FPFog && RenderSettings.fog) RenderSettings.fog = false;
        float fs = RenderSettings.fogStartDistance;
        if (fs != lastFogStart)
        {
            Debug.Log("[FirstPersonLoD] VR: game set fog start " + lastFogStart.ToString("0.#") + " -> " + fs.ToString("0.#")
                + (FPConfig.FPFog ? "" : " (fog stays off in first person)"));
            lastFogStart = fs;
        }

        if (Time.realtimeSinceStartup < nextLightPass) return;
        nextLightPass = Time.realtimeSinceStartup + 1f;
        UnityEngine.Object[] ls = SceneLights.All();
        sortFrom = playerPos;
        bool showcase = InShowcase;
        // the Tavern showcase draws with the game's deferred path: every light per pixel at a cost
        // that does not grow with the number of bricks, and cheap shadows (measured by the perf probe:
        // deferred + 1 shadow ran at 90 fps where forward + 1 shadow ran at 72). Deferred has no
        // multisample anti-aliasing, so edges are a little harder there.
        if (applied && cam != null && FPConfig.TavernDeferred)
        {
            RenderingPath want = showcase ? RenderingPath.DeferredShading : (FPConfig.FPRenderPath == "forward" ? RenderingPath.Forward : savedPath);
            if (cam.renderingPath != want)
            {
                cam.renderingPath = want;
                Debug.Log("[FirstPersonLoD] VR Tavern showcase: rendering path " + want + (showcase ? " (all lights per pixel, nearest shadow)" : " (back to the first-person budget)"));
            }
        }
        int cap = showcase ? Mathf.Max(FPConfig.FPPixelLights, FPConfig.TavernPixelLights) : FPConfig.FPPixelLights;
        if (applied && cap >= 0 && QualitySettings.pixelLightCount != cap) QualitySettings.pixelLightCount = cap;
        NearLights(ls);
        // the deferred path draws shadows cheaply (the perf probe: deferred + 1 shadow at 89-90 fps):
        // the nearest caster keeps its shadows there, so the walls, ceilings and crates catch them
        bool deferred = cam != null && cam.actualRenderingPath == RenderingPath.DeferredShading;
        int keepShadows = FPConfig.FPShadowLights < 0 ? -1 : showcase ? Mathf.Max(1, FPConfig.FPShadowLights)
            : deferred ? Mathf.Max(FPConfig.FPDeferredShadows, FPConfig.FPShadowLights) : FPConfig.FPShadowLights;
        if (keepShadows < 0) return;

        // shadow casters by their original setting, nearest first
        casters.Clear();
        int active = 0;
        for (int i = 0; i < ls.Length; i++)
        {
            Light l = (Light)ls[i];
            if (l == null || l == headLight || !l.enabled || !l.gameObject.activeInHierarchy) continue;
            active++;
            LightShadows orig;
            if (!savedShadows.TryGetValue(l, out orig)) orig = l.shadows;
            if (orig != LightShadows.None) casters.Add(l);
        }
        score.Clear();
        for (int i = 0; i < casters.Count; i++)
        {
            Light l = casters[i];
            // kept casters (not in savedShadows) get the head start
            score[l] = (l.transform.position - sortFrom).magnitude - (savedShadows.ContainsKey(l) ? 0f : FPConfig.LightHysteresis);
        }
        casters.Sort(ByScore);
        int kept = 0;
        for (int i = 0; i < casters.Count; i++)
        {
            Light l = casters[i];
            LightShadows orig;
            bool saved = savedShadows.TryGetValue(l, out orig);
            if (i < keepShadows)
            {
                if (saved) { l.shadows = orig; savedShadows.Remove(l); churn++; }
                if (FPConfig.FPShadowRes > 0 && l.shadowCustomResolution != FPConfig.FPShadowRes)
                {
                    if (!savedRes.ContainsKey(l)) savedRes[l] = l.shadowCustomResolution;
                    l.shadowCustomResolution = FPConfig.FPShadowRes;
                }
                kept++;
            }
            else if (!saved)
            {
                savedShadows[l] = l.shadows;
                l.shadows = LightShadows.None;
                churn++;
            }
        }
        string n = active + " lights, " + forcedNow + " reaching you forced per-pixel (+ head light, at most " + QualitySettings.pixelLightCount + " per object), "
            + casters.Count + " cast shadows, " + kept + " kept" + (kept > 0 && FPConfig.FPShadowRes > 0 ? " at " + FPConfig.FPShadowRes + " px" : "");
        if (n != Note) { Note = n; Debug.Log("[FirstPersonLoD] VR lights: " + n + " (render path " + (cam != null ? cam.actualRenderingPath.ToString() : "?") + ", pixel light cap " + QualitySettings.pixelLightCount + ")"); }
    }

    private static int forcedNow;
    private static string nearNote = "";
    public static string NearNote { get { return nearNote; } }

    // Forward rendering picks at most pixelLightCount lights per object by its own ranking, which
    // for large room meshes often drops the lamp you are standing next to. The lights whose reach
    // covers the player are marked Important, which Unity always renders per-pixel.
    private static void NearLights(UnityEngine.Object[] ls)
    {
        near.Clear();
        for (int i = 0; i < ls.Length; i++)
        {
            Light l = (Light)ls[i];
            if (l == null || l == headLight || !l.enabled || !l.gameObject.activeInHierarchy) continue;
            if (l.type != LightType.Point && l.type != LightType.Spot) continue;
            near.Add(l);
        }
        score.Clear();
        float margin = FPConfig.LightHysteresis;
        for (int i = 0; i < near.Count; i++) score[near[i]] = Reach(near[i]) - (savedModes.ContainsKey(near[i]) ? margin : 0f);
        near.Sort(ByScore);
        forcedNow = 0;
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < near.Count; i++)
        {
            Light l = near[i];
            LightRenderMode orig;
            bool saved = savedModes.TryGetValue(l, out orig);
            bool want = i < FPConfig.FPNearLights && score[l] < 0f;
            if (want)
            {
                if (!saved) { savedModes[l] = l.renderMode; churn++; }
                if (l.renderMode != LightRenderMode.ForcePixel) l.renderMode = LightRenderMode.ForcePixel;
                forcedNow++;
                if (forcedNow <= 3) sb.Append((sb.Length > 0 ? ", " : "") + l.name + " " + (-Reach(l)).ToString("0.#") + " inside");
            }
            else if (saved) { l.renderMode = orig; savedModes.Remove(l); churn++; }
        }
        nearNote = sb.Length > 0 ? sb.ToString() : "none reach you";
    }

    // Picture quality, paid for with graphics-card time the game leaves idle (about 3.5 ms of the
    // 11.1 ms it has per frame at 90 Hz). None of these add work for the main thread:
    //   FPRenderScale  eyes rendered at this multiple of SteamVR's size (default 0: left to SteamVR's
    //                  own resolution setting; v0.8.6's 1.25 made 3305x3305 eyes on the Frame)
    //   FPMsaa         multisample anti-aliasing (default -1: left at the game's own 4x)
    //   FPAniso        anisotropic filtering: floors and walls seen at a slant stay sharp
    // All three go back to the game's own values in the tabletop view.
    private static float savedRenderScale = -1f;
    private static int savedAA = -1;
    private static AnisotropicFiltering savedAniso;
    private static bool visualsApplied, savedAllowMsaa, msaaTouched;
    private static int visualsLogFrame = -1;
    private static void ApplyVisuals(Camera c)
    {
        try
        {
            if (visualsApplied) return;
            visualsApplied = true;
            savedRenderScale = UnityEngine.VR.VRSettings.renderScale;
            savedAA = QualitySettings.antiAliasing;
            savedAniso = QualitySettings.anisotropicFiltering;
            if (FPConfig.FPRenderScale > 0f) UnityEngine.VR.VRSettings.renderScale = Mathf.Clamp(FPConfig.FPRenderScale, 0.5f, 2f);
            if (FPConfig.FPMsaa >= 0) QualitySettings.antiAliasing = FPConfig.FPMsaa;
            if (FPConfig.FPAniso) QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            msaaTouched = c != null && FPConfig.FPMsaa >= 0;
            if (msaaTouched) { savedAllowMsaa = c.allowMSAA; c.allowMSAA = FPConfig.FPMsaa > 0; }
            visualsLogFrame = Time.frameCount + 30;   // report the eye size once the new textures exist
            Debug.Log("[FirstPersonLoD] VR picture: render scale " + savedRenderScale.ToString("0.##") + " -> " + UnityEngine.VR.VRSettings.renderScale.ToString("0.##")
                + ", anti-aliasing " + savedAA + "x -> " + QualitySettings.antiAliasing + "x, anisotropic filtering " + savedAniso + " -> " + QualitySettings.anisotropicFiltering);
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] VR picture settings failed: " + e.GetType().Name + ": " + e.Message); }
    }

    public static void VisualsTick()
    {
        if (visualsLogFrame < 0 || Time.frameCount < visualsLogFrame) return;
        visualsLogFrame = -1;
        try { Debug.Log("[FirstPersonLoD] VR picture: each eye now renders " + UnityEngine.VR.VRSettings.eyeTextureWidth + " x " + UnityEngine.VR.VRSettings.eyeTextureHeight + " pixels"); } catch (Exception) { }
    }

    private static void RestoreVisuals()
    {
        if (!visualsApplied) return;
        visualsApplied = false;
        try
        {
            if (savedRenderScale > 0f) UnityEngine.VR.VRSettings.renderScale = savedRenderScale;
            if (savedAA >= 0) QualitySettings.antiAliasing = savedAA;
            QualitySettings.anisotropicFiltering = savedAniso;
            if (cam != null && msaaTouched) cam.allowMSAA = savedAllowMsaa;
        }
        catch (Exception) { }
    }

    public static void Restore()
    {
        RestoreVisuals();
        if (headLight != null) headLight.enabled = false;
        foreach (KeyValuePair<Light, LightRenderMode> kv in savedModes)
            if (kv.Key != null) kv.Key.renderMode = kv.Value;
        savedModes.Clear();
        foreach (KeyValuePair<Light, int> kv in savedRes)
            if (kv.Key != null) kv.Key.shadowCustomResolution = kv.Value;
        savedRes.Clear();
        if (!applied) return;
        applied = false;
        RenderSettings.fog = savedFog;
        QualitySettings.pixelLightCount = savedPixelLights;
        QualitySettings.shadowDistance = savedShadowDistance;
        if (cam != null) { cam.renderingPath = savedPath; cam.useOcclusionCulling = savedOcclusion; }
        for (int i = 0; i < offCams.Count; i++) if (offCams[i] != null) offCams[i].enabled = true;
        offCams.Clear();
        throttled.Clear();
        cam = null;
        RenderSettings.ambientLight = savedAmbient;
        RenderSettings.ambientEquatorColor = savedEquator;
        RenderSettings.ambientGroundColor = savedGround;
        RenderSettings.ambientIntensity = savedAmbientIntensity;
        foreach (KeyValuePair<Light, LightShadows> kv in savedShadows)
            if (kv.Key != null) kv.Key.shadows = kv.Value;
        savedShadows.Clear();
        Note = "";
    }
}

// ---------------------------------------------------------------------------------------------
// Hands. A simple gloved block on each controller so you can see where your hands are: always on
// the left (the HUD sits on its wrist), on the right only when the held item draws nothing (fists,
// pet whistle). Colliders are removed so they never touch game physics.
// ---------------------------------------------------------------------------------------------
public static class VRHands
{
    private static GameObject left, right;
    private static Material mat;
    private static bool failed;
    public static Transform LeftHand { get { return left != null && left.activeSelf ? left.transform : null; } }
    private static Vector3 lastLP, lastRP;
    private static Quaternion lastLR = Quaternion.identity, lastRR = Quaternion.identity;
    private static float lastLAt = -10f, lastRAt = -10f;

    private static GameObject Make(string name)
    {
        GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name = name;
        Collider c = g.GetComponent<Collider>();
        if (c != null) UnityEngine.Object.DestroyImmediate(c);
        g.AddComponent<FPThickLayer>();
        UnityEngine.Object.DontDestroyOnLoad(g);
        MeshRenderer r = g.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return g;
    }

    private static bool Ensure()
    {
        if (left != null && right != null) return true;
        if (failed) return false;
        if (mat == null)
        {
            Shader sh = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Diffuse") ?? Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse");
            if (sh == null) { failed = true; Debug.Log("[FirstPersonLoD] VR hands: no usable shader, hands not shown"); return false; }
            mat = new Material(sh);
            mat.color = new Color(0.42f, 0.28f, 0.18f);
            Debug.Log("[FirstPersonLoD] VR hands: shader " + sh.name);
        }
        if (left == null) left = Make("FPLeftHand");
        if (right == null) right = Make("FPRightHand");
        return true;
    }

    private static void Place(GameObject g, bool isRight, bool want, Transform space, float worldScale,
        ref Vector3 lp, ref Quaternion lr, ref float at)
    {
        Vector3 hp; Quaternion hr;
        if (VRHand.Get(isRight, out hp, out hr)) { lp = hp; lr = hr; at = Time.realtimeSinceStartup; }
        bool show = want && Time.realtimeSinceStartup - at < 1f;
        if (g.activeSelf != show) g.SetActive(show);
        if (!show) return;
        Vector3 size = FPConfig.HandSize * worldScale;
        g.transform.position = space.TransformPoint(lp + lr * (FPConfig.GripOffset + new Vector3(0f, 0f, -FPConfig.HandSize.z * 0.25f)));
        g.transform.rotation = space.rotation * lr;
        g.transform.localScale = size;
    }

    public static void Tick(Transform head, float worldScale)
    {
        if (!FPConfig.ShowHands || head == null || head.parent == null || !Ensure()) { Release(); return; }
        Place(left, false, true, head.parent, worldScale, ref lastLP, ref lastLR, ref lastLAt);
        Place(right, true, !Sword6.HasVisual, head.parent, worldScale, ref lastRP, ref lastRR, ref lastRAt);
    }

    public static void Release()
    {
        if (left != null && left.activeSelf) left.SetActive(false);
        if (right != null && right.activeSelf) right.SetActive(false);
    }
}


// ---------------------------------------------------------------------------------------------
// Sound device changes. When Windows switches or re-creates the sound output (a recording tool
// starting, SteamVR's streaming speakers restarting, a different default device), Unity resets
// its audio and every sound that was playing stops; this old game never restarts its music (a
// dozen layers playing in step, MusicControl.Sources), so the dungeon goes quiet for good. The
// mod notes every change in the log and restarts the music layers that were playing, in step,
// where they would have been. Checked once a second: which music layers are playing and where.
// ---------------------------------------------------------------------------------------------
public static class AudioGuard
{
    private sealed class Snap { public AudioSource a; public int samples; }
    private static readonly List<Snap> snaps = new List<Snap>();
    private static bool installed, pending;
    private static float pendingAt, nextSnap, snapAt, nextFind;
    private static int changes, restarted, logs;
    private static MusicControl mc;
    private static int lastPlaying, lastLayers;

    public static string Install()
    {
        if (!FPConfig.AudioGuard) return "off (AudioGuard=false)";
        try
        {
            AudioSettings.OnAudioConfigurationChanged += Changed;
            installed = true;
            AudioConfiguration c = AudioSettings.GetConfiguration();
            return "on (music restarted if the sound device changes); output " + c.sampleRate + " Hz " + c.speakerMode + ", buffer " + c.dspBufferSize;
        }
        catch (Exception e) { return "failed: " + e.GetType().Name + ": " + e.Message; }
    }

    private static void Changed(bool deviceWasChanged)
    {
        changes++;
        pending = true;
        pendingAt = Time.realtimeSinceStartup;
        try
        {
            AudioConfiguration c = AudioSettings.GetConfiguration();
            if (logs++ < 40) Debug.Log("[FirstPersonLoD] Sound: the audio output was reset (" + (deviceWasChanged ? "Windows changed the sound device" : "settings changed")
                + "): now " + c.sampleRate + " Hz " + c.speakerMode + ", buffer " + c.dspBufferSize + "; " + snaps.Count + " music layers were playing " + (Time.realtimeSinceStartup - snapAt).ToString("0.0") + " s ago");
        }
        catch (Exception) { }
    }

    public static void Tick()
    {
        if (!installed) return;
        float now = Time.realtimeSinceStartup;
        if (pending && now - pendingAt > 0.25f) { pending = false; Restore(now); }
        if (!pending && now >= nextSnap) { nextSnap = now + 1f; Snapshot(now); }
    }

    private static void Snapshot(float now)
    {
        try
        {
            if (mc == null)
            {
                if (now < nextFind) return;
                nextFind = now + 10f;
                mc = (MusicControl)UnityEngine.Object.FindObjectOfType(typeof(MusicControl));
                if (mc == null) return;
            }
            if (mc.Sources == null) return;
            snaps.Clear();
            int layers = 0;
            IList<MusicControl.MData> vals = mc.Sources.Values;
            for (int i = 0; i < vals.Count; i++)
            {
                GameObject g = vals[i].source;
                if (g == null) continue;
                AudioSource a = g.GetComponent<AudioSource>();
                if (a == null || a.clip == null) continue;
                layers++;
                if (!a.isPlaying) continue;
                Snap s = new Snap(); s.a = a; s.samples = a.timeSamples;
                snaps.Add(s);
            }
            snapAt = now;
            lastPlaying = snaps.Count; lastLayers = layers;
        }
        catch (Exception) { }
    }

    private static void Restore(float now)
    {
        int n = 0, already = 0;
        try
        {
            // one shared start, each layer where it would be by then: they stay in step
            double startDsp = AudioSettings.dspTime + 0.1;
            float elapsed = now - snapAt + 0.1f;
            for (int i = 0; i < snaps.Count; i++)
            {
                Snap s = snaps[i];
                if (s.a == null || s.a.clip == null) continue;
                if (s.a.isPlaying) { already++; continue; }
                int len = s.a.clip.samples;
                if (len <= 0) continue;
                long pos = s.samples + (long)(elapsed * s.a.clip.frequency);
                s.a.timeSamples = (int)(pos % len);
                s.a.PlayScheduled(startDsp);
                n++;
            }
        }
        catch (Exception e) { Debug.Log("[FirstPersonLoD] Sound: restarting the music failed: " + e.GetType().Name + ": " + e.Message); }
        restarted += n;
        if (logs++ < 40) Debug.Log("[FirstPersonLoD] Sound: restarted " + n + " music layers" + (already > 0 ? " (" + already + " were still playing)" : "") + " after the output reset");
    }

    public static string Status()
    {
        if (!installed) return "audio guard off";
        return "music " + lastPlaying + " of " + lastLayers + " layers playing, " + changes + " sound device resets" + (restarted > 0 ? " (" + restarted + " layers restarted)" : "")
            + ", listener volume " + AudioListener.volume.ToString("0.00") + (AudioListener.pause ? " PAUSED" : "");
    }
}

// ---------------------------------------------------------------------------------------------
// Pickup magnet (first person). The game picks things up when your body's collider touches their
// trigger box, which is easy from the side view (one lane) and fiddly in first person, where the
// room has depth and coins scatter across it. Coins (CoinGet) and floor items (PickupItem) within
// PickupRadius across the floor slide to your body, and the game's own trigger then picks them up
// exactly as if you had walked onto them (gold, items, sounds, full-inventory rules, all the
// game's). Left alone: shop stock (anything under a shop display, held still by the game), chest
// "coins", anything in the Tavern, and what you just dropped (until you have walked away from it).
// ---------------------------------------------------------------------------------------------
public static class PickupMagnet
{
    private sealed class It { public Component c; public Transform t; public Rigidbody rb; public bool dropped, coin, skip; }
    private static readonly Dictionary<int, It> items = new Dictionary<int, It>();
    private static readonly List<int> gone = new List<int>();
    private static readonly Collider[] buf = new Collider[512];
    private static readonly Dictionary<int, bool> notItem = new Dictionary<int, bool>();
    private static float nextScan, lastDrop = -10f;
    private static int logs, pulledNow;

    public static void DropPressed() { lastDrop = Time.realtimeSinceStartup; }

    private static bool ShopStock(Transform t, Rigidbody rb)
    {
        if (rb != null && rb.isKinematic) return true;
        for (Transform p = t; p != null; p = p.parent) if (p.GetComponent<shopitem>() != null || p.GetComponent<ItemShop>() != null) return true;
        return false;
    }

    private static void Add(Component c, bool coin, Vector3 target, float now)
    {
        int id = c.gameObject.GetInstanceID();
        if (items.ContainsKey(id)) return;
        It it = new It();
        it.c = c; it.t = c.transform; it.rb = c.GetComponent<Rigidbody>(); it.coin = coin;
        Vector3 d = target - it.t.position; d.y = 0f;
        // appeared at your feet right after a drop: yours, until you walk away from it
        it.dropped = !coin && now - lastDrop < 2f && d.magnitude < 2f;
        if (!coin && ShopStock(it.t, it.rb)) it.skip = true;   // remembered, never pulled
        items[id] = it;
    }

    public static void Tick(GameObject player)
    {
        if (!FPConfig.PickupMagnet || player == null) return;
        if (VRFP.CurrentRoomName == "Tavern") return;
        Collider pc = player.GetComponent<Collider>();
        Vector3 target = pc != null ? pc.bounds.center : player.transform.position + Vector3.up * 0.5f;
        float now = Time.realtimeSinceStartup;
        float R = FPConfig.PickupRadius, dt = Time.deltaTime;
        if (now >= nextScan)
        {
            // v0.9.7 scanned every object in the game for pickups four times a second (about 4 ms a
            // frame on average); a physics query around you finds the same things for next to nothing
            nextScan = now + 0.1f;
            int n = Physics.OverlapSphereNonAlloc(target, R + 1f, buf, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                Collider col = buf[i];
                if (col == null) continue;
                int cid = col.GetInstanceID();
                if (notItem.ContainsKey(cid)) continue;
                PickupItem pi = col.GetComponent<PickupItem>();
                if (pi != null) { Add(pi, false, target, now); continue; }
                CoinGet cg = col.GetComponent<CoinGet>();
                if (cg == null && col.transform.parent != null) cg = col.transform.parent.GetComponent<CoinGet>();
                if (cg != null) { if (!cg.chest) Add(cg, true, target, now); continue; }
                if (notItem.Count > 20000) notItem.Clear();
                notItem[cid] = true;
            }
            // picked up, gone, or left far behind: forget it
            gone.Clear();
            foreach (KeyValuePair<int, It> kv in items)
            {
                It it = kv.Value;
                if (it.c == null) { gone.Add(kv.Key); continue; }
                Vector3 d = target - it.t.position; d.y = 0f;
                if (d.magnitude > R + 3f) gone.Add(kv.Key);
            }
            for (int i = 0; i < gone.Count; i++) items.Remove(gone[i]);
        }
        int pulling = 0;
        foreach (KeyValuePair<int, It> kv in items)
        {
            It it = kv.Value;
            if (it.skip || it.c == null || !it.t.gameObject.activeInHierarchy) continue;
            Vector3 p = it.t.position;
            Vector3 d = target - p;
            float across = new Vector2(d.x, d.z).magnitude;
            if (it.dropped) { if (across > R + 1f) it.dropped = false; continue; }
            if (across > R || d.y > 2.5f || d.y < -1.5f) continue;
            pulling++;
            float dist = d.magnitude;
            float step = Mathf.Min(dist, FPConfig.PickupPull * dt);
            Vector3 np = p + d / Mathf.Max(1e-4f, dist) * step;
            if (it.rb != null && !it.rb.isKinematic) { it.rb.velocity = Vector3.zero; it.rb.position = np; }
            it.t.position = np;
        }
        if (pulling > 0 && pulledNow == 0 && logs++ < 20) Debug.Log("[FirstPersonLoD] VR pickup magnet: pulling " + pulling + " thing(s) within " + R.ToString("0.0") + " units to you");
        pulledNow = pulling;
    }
}

// ---------------------------------------------------------------------------------------------
// The free hand does things. Switches (and the other things the game makes you hit to work:
// OffHandTargets) take a hit from the free hand: press its trigger with the hand on the switch,
// or pointing at it from close by, and the switch gets the same "Hit" the game's own punch sends
// it. Found in two steps: anything within OffHandReach of the hand, else the first thing along
// where the hand points, up to OffHandPoint. The left trigger is otherwise "previous item": on
// the Steam Frame the D-pad already does that, so the trigger belongs to the hand alone; on
// other controllers it still picks the previous item when nothing is in reach. Every press is
// logged with what was found (or what was near), so reach can be tuned.
// ---------------------------------------------------------------------------------------------
public static class OffHandUse
{
    private static string ctype;
    private static string[] kinds; private static string kindsSrc;
    public static bool IsFrame { get { return ControllerType() == "frame_controller"; } }
    private static readonly Dictionary<int, float> lastHit = new Dictionary<int, float>();
    private static int logged;

    public static string ControllerType()
    {
        if (ctype != null) return ctype;
        ctype = "";
        try
        {
            Valve.VR.CVRSystem sys = Valve.VR.OpenVR.System;
            if (sys == null) { ctype = null; return ""; }
            StringBuilder prop = new StringBuilder(256);
            for (uint i = 0; i < 64; i++)
            {
                if (sys.AAAAAAAAAAAAAAAAAAAAss(i) /* GetTrackedDeviceClass */ != Valve.VR.ETrackedDeviceClass.Controller) continue;
                Valve.VR.ETrackedPropertyError err = Valve.VR.ETrackedPropertyError.TrackedProp_Success;
                prop.Length = 0;
                sys.AAAAAAAAAAAAAAAAAAAAAqw( /* GetStringTrackedDeviceProperty */ i, Valve.VR.ETrackedDeviceProperty.Prop_ControllerType_String, prop, 256, ref err);
                if (prop.Length > 0) { ctype = prop.ToString(); break; }
            }
        }
        catch (Exception) { }
        if (ctype.Length == 0) ctype = null;   // try again later (controllers not on yet)
        return ctype ?? "";
    }

    private static bool Wanted(MonoBehaviour mb)
    {
        if (mb == null) return false;
        if (kinds == null || kindsSrc != FPConfig.OffHandTargets)
        {
            kindsSrc = FPConfig.OffHandTargets;
            List<string> o = new List<string>();
            foreach (string p in kindsSrc.Split(',')) { string k = p.Trim(); if (k.Length > 0) o.Add(k); }
            kinds = o.ToArray();
        }
        string n = mb.GetType().Name;
        for (int i = 0; i < kinds.Length; i++) if (n == kinds[i]) return true;
        return false;
    }

    // the switch (or other target) a collider belongs to, walking up a few parents
    private static MonoBehaviour Target(Collider c)
    {
        Transform t = c.transform;
        for (int up = 0; up < 4 && t != null; up++, t = t.parent)
        {
            MonoBehaviour[] mbs = t.GetComponents<MonoBehaviour>();
            for (int i = 0; i < mbs.Length; i++) if (Wanted(mbs[i])) return mbs[i];
        }
        return null;
    }

    // runs in the VRInputs postfix, before the D-pad is mapped (so a D-pad "previous" survives)
    public static void Filter(VRInputs vi, Transform head)
    {
        if (!FPConfig.OffHandUse || vi == null || !vi.Prev || head == null || head.parent == null) return;
        if (FPConfig.WeaponHand == "left" || InvMenu.Blocking) return;
        bool frame = IsFrame;
        Vector3 lp; Quaternion lr;
        if (!VRHand.Get(false, out lp, out lr))
        {
            if (frame) vi.Prev = false;
            if (logged++ < 30) Debug.Log("[FirstPersonLoD] VR free hand: trigger pressed but the left controller is not tracking");
            return;
        }
        Transform space = head.parent;
        Vector3 go = FPConfig.GripOffset; go.x = -go.x;
        Vector3 handT = lp + lr * (go + new Vector3(0f, 0f, 0.05f));      // the fist, in front of the grip
        Vector3 hand = space.TransformPoint(handT);
        Vector3 dir = (space.rotation * lr) * Vector3.forward;
        float perM = space.TransformVector(Vector3.right).magnitude;       // world units per meter
        MonoBehaviour best = null; float bestD = float.MaxValue; string how = "";
        Collider[] cs = Physics.OverlapSphere(hand, FPConfig.OffHandReach * perM, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < cs.Length; i++)
        {
            MonoBehaviour t = Target(cs[i]);
            if (t == null) continue;
            float d = (cs[i].ClosestPointOnBounds(hand) - hand).magnitude;
            if (d < bestD) { bestD = d; best = t; how = "touching"; }
        }
        if (best == null && FPConfig.OffHandPoint > 0f)
        {
            RaycastHit[] hs = Physics.SphereCastAll(hand, 0.06f * perM, dir, FPConfig.OffHandPoint * perM, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hs.Length; i++)
            {
                MonoBehaviour t = Target(hs[i].collider);
                if (t == null) continue;
                if (hs[i].distance < bestD) { bestD = hs[i].distance; best = t; how = "pointed at"; }
            }
        }
        if (best != null)
        {
            vi.Prev = false;
            int id = best.GetInstanceID();
            float last;
            if (lastHit.TryGetValue(id, out last) && Time.realtimeSinceStartup - last < 0.5f) return;
            lastHit[id] = Time.realtimeSinceStartup;
            best.gameObject.SendMessage("Hit", 1, SendMessageOptions.DontRequireReceiver);
            if (logged++ < 60) Debug.Log("[FirstPersonLoD] VR free hand: hit " + best.GetType().Name + " on " + Path(best.transform) + " (" + how + ", " + (bestD / Mathf.Max(1e-4f, perM) * 100f).ToString("0") + " cm)");
            return;
        }
        if (frame) vi.Prev = false;
        if (logged++ < 60)
        {
            // what is near the hand, so the reach or the target list can be tuned from the log
            StringBuilder sb = new StringBuilder();
            Collider[] near = Physics.OverlapSphere(hand, 0.6f * perM, ~0, QueryTriggerInteraction.Collide);
            int shown = 0;
            for (int i = 0; i < near.Length && shown < 8; i++)
            {
                MonoBehaviour[] mbs = near[i].GetComponentsInParent<MonoBehaviour>();
                string types = "";
                for (int k = 0; k < mbs.Length && k < 3; k++) if (mbs[k] != null) types += (types.Length > 0 ? "," : "") + mbs[k].GetType().Name;
                if (types.Length == 0 || near[i].name == "SmallBlock") continue;
                sb.Append("\n  " + Path(near[i].transform) + " [" + types + "] " + ((near[i].ClosestPointOnBounds(hand) - hand).magnitude / Mathf.Max(1e-4f, perM) * 100f).ToString("0") + " cm");
                shown++;
            }
            Debug.Log("[FirstPersonLoD] VR free hand: trigger, nothing to hit within " + (FPConfig.OffHandReach * 100f).ToString("0") + " cm or pointed at within " + (FPConfig.OffHandPoint * 100f).ToString("0") + " cm"
                + (frame ? "" : " (previous item instead)") + (sb.Length > 0 ? "; near the hand (60 cm):" + sb : "; nothing with a script within 60 cm"));
        }
    }

    private static string Path(Transform t)
    {
        return (t.parent != null ? t.parent.name + "/" : "") + t.name;
    }
}

// ---------------------------------------------------------------------------------------------
// Off hand. Some items carry a second piece for the other hand: the Knight's shield is its own
// "Shield" renderer under the KnightSword item (the game has no blocking, it is art only). First
// person hides it with the rest of the body, so it is rebuilt as 3D pixel blocks (like the sword)
// from the same sprite frame and held in the left controller: its face points where your fist
// points, upright when your thumb points up.
// ---------------------------------------------------------------------------------------------
public static class OffHand
{
    private static GameObject anchor, forPlayer;
    private static Renderer src;
    private static Material mat;
    private static Mesh mesh;
    private static float nextScan;
    private static Vector3 lastP;
    private static Quaternion lastR = Quaternion.identity;
    private static float lastAt = -10f;
    public static string Note = "none";

    // v0.6.6 skipped everything under the held item, but the Knight's "Shield" is a child of the
    // KnightSword item (it comes and goes with it), so it was never found
    private static Renderer Find(GameObject player, GameObject held)
    {
        Renderer[] rs = player.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null || !(r is MeshRenderer)) continue;
            string n = r.name.ToLowerInvariant();
            for (int j = 0; j < FPConfig.OffHandNames.Length; j++)
                if (FPConfig.OffHandNames[j].Length > 0 && n.Contains(FPConfig.OffHandNames[j])) return r;
        }
        return null;
    }

    private static void Unbuild()
    {
        if (anchor != null) UnityEngine.Object.Destroy(anchor);
        if (mat != null) UnityEngine.Object.Destroy(mat);
        if (mesh != null) UnityEngine.Object.Destroy(mesh);
        anchor = null; mat = null; mesh = null;
    }

    private static void Build()
    {
        MeshFilter smf = src.GetComponent<MeshFilter>();
        Material sm = FPDriver.OriginalMaterial(src);
        Texture tex = sm != null ? sm.mainTexture : null;
        if (smf == null || smf.sharedMesh == null || tex == null) { Note = src.name + ": no mesh or texture"; Debug.Log("[FirstPersonLoD] VR off hand: " + Note); return; }
        Vector2 off = sm.mainTextureOffset, sc = sm.mainTextureScale;
        if (Mathf.Abs(sc.x) < 0.0001f || Mathf.Abs(sc.y) < 0.0001f) sc = Vector2.one;
        int W = tex.width, H = tex.height;
        float ax = 1f, bx = -0.5f, ay = 1f, by = -0.5f;
        try
        {
            Vector3[] v = smf.sharedMesh.vertices; Vector2[] uv = smf.sharedMesh.uv;
            int iu0 = 0, iu1 = 0, iv0 = 0, iv1 = 0;
            for (int i = 1; i < uv.Length; i++)
            {
                if (uv[i].x < uv[iu0].x) iu0 = i; if (uv[i].x > uv[iu1].x) iu1 = i;
                if (uv[i].y < uv[iv0].y) iv0 = i; if (uv[i].y > uv[iv1].y) iv1 = i;
            }
            if (uv.Length >= 3 && uv[iu1].x - uv[iu0].x > 0.0001f && uv[iv1].y - uv[iv0].y > 0.0001f)
            {
                ax = (v[iu1].x - v[iu0].x) / (uv[iu1].x - uv[iu0].x); bx = v[iu0].x - ax * uv[iu0].x;
                ay = (v[iv1].y - v[iv0].y) / (uv[iv1].y - uv[iv0].y); by = v[iv0].y - ay * uv[iv0].y;
            }
        }
        catch (Exception) { }
        Vector3 ls = src.transform.lossyScale;
        float sx = Mathf.Abs(ls.x) * FPConfig.OffHandScale, sy = Mathf.Abs(ls.y) * FPConfig.OffHandScale;
        Color32[] px = Sword6.ReadTexture(tex);
        float u0 = Mathf.Min(off.x, off.x + sc.x), v0 = Mathf.Min(off.y, off.y + sc.y);
        int fw = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(sc.x) * W)), fh = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(sc.y) * H));
        int fx = Mathf.RoundToInt(u0 * W), fy = Mathf.RoundToInt(v0 * H);
        bool[] solid = new bool[fw * fh];
        int n = 0; Vector2 sum = Vector2.zero, mn = new Vector2(float.MaxValue, float.MaxValue), mx = new Vector2(float.MinValue, float.MinValue);
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                int tx = ((fx + x) % W + W) % W, ty = ((fy + y) % H + H) % H;
                if (px[ty * W + tx].a < 128) continue;
                solid[y * fw + x] = true;
                float mu = ((fx + x + 0.5f) / W - off.x) / sc.x, mv = ((fy + y + 0.5f) / H - off.y) / sc.y;
                Vector2 q = new Vector2((ax * mu + bx) * sx, (ay * mv + by) * sy);
                sum += q; n++; mn = Vector2.Min(mn, q); mx = Vector2.Max(mx, q);
            }
        if (n < 3) { Note = src.name + ": frame has no opaque pixels"; Debug.Log("[FirstPersonLoD] VR off hand: " + Note); return; }
        Vector2 centre = sum / n;
        mesh = Sword6.BuildVoxels(solid, fw, fh, fx, fy, W, H, off, sc, ax, bx, ay, by, sx, sy);
        string matNote;
        mat = Sword6.HandMaterial(sm, out matNote);
        anchor = new GameObject("FPOffHand");
        UnityEngine.Object.DontDestroyOnLoad(anchor);
        GameObject g = new GameObject("voxels");
        g.AddComponent<FPThickLayer>();
        g.layer = src.gameObject.layer;
        g.transform.SetParent(anchor.transform, false);
        g.transform.localPosition = -new Vector3(centre.x, centre.y, 0f);
        g.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer mr = g.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        Sword6.CheapRenderer(mr);
        anchor.SetActive(false);
        float ws = Mathf.Max(VRFP.WorldScale, 0.0001f);
        Vector2 size = mx - mn;
        Note = src.name + " (" + (src.transform.parent != null ? src.transform.parent.name : "root") + "): " + n + " opaque px, " + (size.x / ws).ToString("0.00") + " x " + (size.y / ws).ToString("0.00")
            + " m, frame " + fw + "x" + fh + " px at " + fx + "," + fy + " of " + W + "x" + H + ", " + matNote
            + ", drawn as 3D blocks (" + mesh.vertexCount + " verts) on the left hand";
        Debug.Log("[FirstPersonLoD] VR off hand: " + Note);
    }

    public static void Tick(GameObject player, Transform head, GameObject held, bool inHand)
    {
        if (!FPConfig.ShowOffHand || player == null || head == null || head.parent == null) { Release(); return; }
        float now = Time.realtimeSinceStartup;
        if (player != forPlayer || now >= nextScan)
        {
            nextScan = now + 2f;
            Renderer found = Find(player, held);
            if (player != forPlayer || found != src)
            {
                Unbuild();
                forPlayer = player;
                src = found;
                if (src != null) { try { Build(); } catch (Exception e) { Unbuild(); Debug.Log("[FirstPersonLoD] VR off hand: build failed " + e.GetType().Name + ": " + e.Message); } }
                else Note = "none on " + player.name;
            }
        }
        // the game can remove it (dropped, class change): follow what the character shows. With the
        // flat weapon fallback the held item stays visible on the body, so no copy is needed
        if (anchor == null || src == null || !src.gameObject.activeInHierarchy) { Release(); return; }
        if (!inHand && held != null && src.transform.IsChildOf(held.transform)) { Release(); return; }
        Vector3 hp; Quaternion hr;
        if (VRHand.Get(false, out hp, out hr)) { lastP = hp; lastR = hr; lastAt = now; }
        else if (now - lastAt > 1f) { Release(); return; }
        else { hp = lastP; hr = lastR; }
        Transform space = head.parent;
        if (!anchor.activeSelf) anchor.SetActive(true);
        anchor.transform.position = space.TransformPoint(hp + hr * FPConfig.OffHandOffset);
        anchor.transform.rotation = space.rotation * hr * Quaternion.Euler(FPConfig.OffHandAngles);
    }

    public static void Release()
    {
        if (anchor != null && anchor.activeSelf) anchor.SetActive(false);
    }
}

// ---------------------------------------------------------------------------------------------
// HUD (health, money, selected item) as a wrist screen.
// The game's player-1 HUD is an old NGUI (2.x) panel. Its geometry is drawn by root-level
// "_UIDrawCall" objects that NGUI places itself from cached matrices, and its layout is anchored to
// the game's UI camera inside the VR rig this mod moves. Moving the panel therefore never moved
// what you see (it ended up in the ceiling). Instead the HUD is left exactly where the game keeps
// it, moved to its own layer, hidden from your eyes, and filmed by a small orthographic camera
// that frames wherever NGUI draws it. That picture is shown on a small screen on the back of the
// left hand, turned to face your eyes. Layers, camera mask and objects are restored on release.
// ---------------------------------------------------------------------------------------------
public static class VRHud
{
    private static GameObject guiGo;
    private static readonly Dictionary<GameObject, int> savedLayers = new Dictionary<GameObject, int>();
    private static UIPanel[] panels = new UIPanel[0];
    private static Camera eyeCam;
    private static int savedMask;
    private static Camera cap;
    private static RenderTexture rt;
    private static GameObject screen;
    private static Material screenMat;
    private static Vector3 wristP;
    private static Quaternion wristR = Quaternion.identity;
    private static float wristAt = -10f, nextWhere, nextRelayer;
    private static int drawn;
    public static string Note = "not placed";

    private static void SetLayer(GameObject g, int layer)
    {
        if (!savedLayers.ContainsKey(g)) savedLayers[g] = g.layer;
        g.layer = layer;
        foreach (Transform c in g.transform) SetLayer(c.gameObject, layer);
    }

    private static bool Setup(CamOTron cot, Camera headCam)
    {
        guiGo = cot.p1gui;
        int L = Mathf.Clamp(FPConfig.HudLayer, 8, 31);
        SetLayer(guiGo, L);
        panels = guiGo.GetComponentsInChildren<UIPanel>(true);
        eyeCam = headCam;
        savedMask = eyeCam.cullingMask;
        eyeCam.cullingMask = savedMask & ~(1 << L);

        rt = new RenderTexture(512, 256, 16, RenderTextureFormat.ARGB32);
        rt.name = "FPHudRT";
        GameObject cg = new GameObject("FPHudCam");
        UnityEngine.Object.DontDestroyOnLoad(cg);
        cap = cg.AddComponent<Camera>();
        cap.stereoTargetEye = StereoTargetEyeMask.None;
        cap.orthographic = true;
        cap.clearFlags = CameraClearFlags.SolidColor;
        Color bg = FPConfig.HudBackground;
        cap.backgroundColor = bg;
        cap.cullingMask = 1 << L;
        cap.depth = -50f;
        cap.targetTexture = rt;
        cap.allowHDR = false;
        cap.allowMSAA = false;
        cap.useOcclusionCulling = false;
        cap.renderingPath = RenderingPath.Forward; // orthographic falls back to forward anyway; no deferred setup

        screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "FPHudScreen";
        Collider col = screen.GetComponent<Collider>();
        if (col != null) UnityEngine.Object.DestroyImmediate(col);
        screen.AddComponent<FPThickLayer>();
        UnityEngine.Object.DontDestroyOnLoad(screen);
        Shader sh = Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Transparent Colored") ?? Shader.Find("Legacy Shaders/Transparent/Diffuse");
        screenMat = new Material(sh);
        screenMat.mainTexture = rt;
        MeshRenderer mr = screen.GetComponent<MeshRenderer>();
        mr.sharedMaterial = screenMat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        Note = "p1gui on layer " + L + " (" + savedLayers.Count + " objects), " + panels.Length + " NGUI panels, screen shader " + (sh != null ? sh.name : "none");
        Debug.Log("[FirstPersonLoD] VR HUD: " + Note);
        return true;
    }

    // frame the camera on everything the HUD's panels actually draw
    private static bool Frame()
    {
        bool any = false;
        Quaternion rot = Quaternion.identity;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        drawn = 0;
        int L = Mathf.Clamp(FPConfig.HudLayer, 8, 31);
        for (int p = 0; p < panels.Length; p++)
        {
            UIPanel pn = panels[p];
            if (pn == null || !pn.gameObject.activeInHierarchy) continue;
            BetterList<UIDrawCall> dcs = pn.drawCalls;
            if (dcs == null) continue;
            for (int i = 0; i < dcs.size; i++)
            {
                UIDrawCall dc = dcs.buffer[i];
                if (dc == null) continue;
                if (dc.gameObject.layer != L)
                {
                    // draw calls are root objects outside p1gui: remember them too, so the hand-back
                    // puts them on their own layer again
                    if (!savedLayers.ContainsKey(dc.gameObject)) savedLayers[dc.gameObject] = dc.gameObject.layer;
                    dc.gameObject.layer = L;
                }
                MeshFilter mf = dc.GetComponent<MeshFilter>();
                MeshRenderer mr = dc.GetComponent<MeshRenderer>();
                if (mf == null || mr == null || !mr.enabled || mf.sharedMesh == null) continue;
                Transform t = dc.transform;
                if (!any) rot = t.rotation;
                Quaternion inv = Quaternion.Inverse(rot);
                Bounds b = mf.sharedMesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 c = new Vector3((k & 1) == 0 ? b.min.x : b.max.x, (k & 2) == 0 ? b.min.y : b.max.y, (k & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 q = inv * t.TransformPoint(c);
                    if (!any) { min = max = q; any = true; } else { min = Vector3.Min(min, q); max = Vector3.Max(max, q); }
                }
                drawn++;
            }
        }
        if (!any) return false;
        Vector3 mid = (min + max) * 0.5f, size = max - min;
        cap.transform.rotation = rot;
        cap.transform.position = rot * new Vector3(mid.x, mid.y, min.z) - (rot * Vector3.forward) * 1f;
        cap.nearClipPlane = 0.01f;
        cap.farClipPlane = size.z + 2f;
        float aspect = rt.width / (float)rt.height;
        cap.orthographicSize = Mathf.Max(size.y * 0.5f, size.x * 0.5f / aspect) * 1.04f + 0.0001f;
        return true;
    }

    public static void Tick(CamOTron cot, Transform head, Camera headCam, float worldScale)
    {
        if (FPConfig.HudMode != "wrist" || cot == null || cot.p1gui == null || head == null || head.parent == null || headCam == null) { Restore(); return; }
        if (guiGo != cot.p1gui || cap == null) { Restore(); if (!Setup(cot, headCam)) return; }
        if (Time.realtimeSinceStartup >= nextRelayer)
        {
            // pieces the game re-enables or adds later
            nextRelayer = Time.realtimeSinceStartup + 1f;
            SetLayer(guiGo, Mathf.Clamp(FPConfig.HudLayer, 8, 31));
        }
        bool framed = Frame();

        Vector3 hp = Vector3.zero; Quaternion hr = Quaternion.identity;
        bool got = VRHand.Get(false, out hp, out hr);
        if (got) { wristP = hp; wristR = hr; wristAt = Time.realtimeSinceStartup; }
        else if (Time.realtimeSinceStartup - wristAt < 1f) { hp = wristP; hr = wristR; got = true; }
        bool show = got && framed;
        if (screen.activeSelf != show) screen.SetActive(show);
        // the HUD changes a few times a second at most: film it every HudEveryFrames frames instead
        // of every frame (one camera render less per frame)
        cap.enabled = false;
        if (!show) return;
        if (Time.frameCount % Mathf.Max(1, FPConfig.HudEveryFrames) == 0) cap.Render();

        Transform space = head.parent;
        Vector3 target = space.TransformPoint(hp + hr * FPConfig.HudWristOffset);
        float w = FPConfig.HudWristWidth * worldScale;
        screen.transform.position = target;
        screen.transform.rotation = Quaternion.LookRotation(target - head.position, head.up);
        screen.transform.localScale = new Vector3(w, w * rt.height / rt.width, 1f);

        if (Time.realtimeSinceStartup >= nextWhere)
        {
            nextWhere = Time.realtimeSinceStartup + 20f;
            Debug.Log("[FirstPersonLoD] VR HUD: filming " + drawn + " NGUI draw calls, frame " + (cap.orthographicSize * 2f).ToString("0.###")
                + " units tall at " + cap.transform.position.ToString("F1") + "; wrist screen " + (target - head.position).magnitude.ToString("0.00") + " units from the eyes");
        }
    }

    public static void Restore()
    {
        foreach (KeyValuePair<GameObject, int> kv in savedLayers) if (kv.Key != null) kv.Key.layer = kv.Value;
        savedLayers.Clear();
        if (eyeCam != null) eyeCam.cullingMask = savedMask;
        eyeCam = null;
        if (cap != null) UnityEngine.Object.Destroy(cap.gameObject);
        if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
        if (screen != null) UnityEngine.Object.Destroy(screen);
        if (screenMat != null) UnityEngine.Object.Destroy(screenMat);
        cap = null; rt = null; screen = null; screenMat = null;
        guiGo = null;
        panels = new UIPanel[0];
    }
}

// ---------------------------------------------------------------------------------------------
// Controller poses straight from the OpenVR compositor (the poses this frame is rendered with, so
// the hand never lags the head), in tracking-space meters with Unity axes. Independent of the
// game's action manifest and bindings.
// ---------------------------------------------------------------------------------------------
public static class VRHand
{
    private static readonly Valve.VR.TrackedDevicePose_t[] render = new Valve.VR.TrackedDevicePose_t[64];
    private static readonly Valve.VR.TrackedDevicePose_t[] game = new Valve.VR.TrackedDevicePose_t[64];
    private static int frame = -1;
    private static bool fetched;
    public static string Error = "";

    private static bool Fetch()
    {
        if (frame == Time.frameCount) return fetched;
        frame = Time.frameCount;
        fetched = false;
        Valve.VR.CVRCompositor comp = Valve.VR.OpenVR.Compositor;
        if (comp == null) { Error = "no compositor"; return false; }
        Valve.VR.EVRCompositorError e = comp.AAAAAAAAAAAAAAAAAAAsh( /* GetLastPoses */ render, game);
        fetched = e == Valve.VR.EVRCompositorError.None;
        if (!fetched) Error = "GetLastPoses " + e;
        return fetched;
    }

    // hand -> device index. The role query is authoritative, but the Meta Link runtime can drop a
    // controller's role while it is still connected; then fall back to the device that reports that
    // role, or whose model name says Left/Right.
    private static readonly uint[] mapped = { uint.MaxValue, uint.MaxValue };
    private static float nextMap;
    private static readonly StringBuilder prop = new StringBuilder(256);

    private static void RefreshMap(Valve.VR.CVRSystem sys)
    {
        if (Time.realtimeSinceStartup < nextMap) return;
        nextMap = Time.realtimeSinceStartup + 2f;
        mapped[0] = mapped[1] = uint.MaxValue;
        for (uint i = 1; i < 16; i++)
        {
            if (sys.AAAAAAAAAAAAAAAAAAAAss(i) /* GetTrackedDeviceClass */ != Valve.VR.ETrackedDeviceClass.Controller) continue;
            Valve.VR.ETrackedControllerRole role = sys.AAAAAAAAAAAAAAAqs(i) /* GetControllerRoleForTrackedDeviceIndex */;
            int hand = role == Valve.VR.ETrackedControllerRole.RightHand ? 1 : role == Valve.VR.ETrackedControllerRole.LeftHand ? 0 : -1;
            if (hand < 0)
            {
                Valve.VR.ETrackedPropertyError err = Valve.VR.ETrackedPropertyError.TrackedProp_Success;
                prop.Length = 0;
                sys.AAAAAAAAAAAAAAAAAAAAAqw( /* GetStringTrackedDeviceProperty */ i, Valve.VR.ETrackedDeviceProperty.Prop_ModelNumber_String, prop, 256, ref err);
                string model = prop.ToString();
                if (model.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0) hand = 1;
                else if (model.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0) hand = 0;
            }
            if (hand >= 0 && mapped[hand] == uint.MaxValue) mapped[hand] = i;
        }
    }

    public static bool Get(bool right, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        Valve.VR.CVRSystem sys = Valve.VR.OpenVR.System;
        if (sys == null || !Fetch()) return false;
        uint idx = sys.AAAAAAAAAAAAAAAqr( /* GetTrackedDeviceIndexForControllerRole */
            right ? Valve.VR.ETrackedControllerRole.RightHand : Valve.VR.ETrackedControllerRole.LeftHand);
        string hand = right ? "right" : "left";
        if (idx >= render.Length)
        {
            RefreshMap(sys);
            idx = mapped[right ? 1 : 0];
            if (idx >= render.Length) { Error = hand + " controller has no role and no device looks like it"; return false; }
        }
        Valve.VR.TrackedDevicePose_t p = render[idx];
        if (!p.bDeviceIsConnected) { Error = hand + " controller (device " + idx + ") disconnected"; return false; }
        if (!p.bPoseIsValid) { Error = hand + " controller (device " + idx + ") not tracking: " + p.eTrackingResult; return false; }
        return Convert(p, out pos, out rot);
    }

    public static bool Hmd(out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        if (!Fetch()) return false;
        return Convert(render[0], out pos, out rot);
    }

    // same conversion as SteamVR_Utils.RigidTransform(HmdMatrix34_t)
    private static bool Convert(Valve.VR.TrackedDevicePose_t p, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        if (!p.bPoseIsValid) { Error = "pose not valid"; return false; }
        Valve.VR.HmdMatrix34_t m = p.mDeviceToAbsoluteTracking;
        pos = new Vector3(m.m3, m.m7, -m.m11);
        Vector3 fwd = new Vector3(-m.m2, -m.m6, m.m10);
        Vector3 up = new Vector3(m.m1, m.m5, -m.m9);
        if (fwd.sqrMagnitude < 0.0001f || up.sqrMagnitude < 0.0001f) return false;
        rot = Quaternion.LookRotation(fwd, up);
        return true;
    }
}

// ---------------------------------------------------------------------------------------------
// Weapon in hand (6DOF). The held item's sprite frame is read once when it is equipped (GPU
// readback, so unreadable textures work), the drawn blade is found by principal-axis analysis of
// its opaque pixels, and the frame is mounted on the controller so the blade runs from your fist
// along the controller. Two crossed planes keep it solid from any angle. The game's attack box is
// moved to the middle of the blade every frame, so hits land where the sword is. A fast swing
// (tip speed over SwingSpeed m/s) presses and releases Use for you; the trigger still works, and
// holding it charges a power attack that the next swing releases.
// ---------------------------------------------------------------------------------------------
public static class Sword6
{
    public static bool Active;
    public static int Inject;                    // read by the VRInputs postfix: 2 press, 1 release
    public static Vector3 BladeForward = Vector3.forward;
    public static Vector3 TipWorld;
    public static string Note = "none";

    private static GameObject item;
    private static bool built;
    private static float builtScale = -1f;
    private static GameObject anchor;
    private static Material mat;
    private static float bladeLen;               // grip to tip, world units
    private static Transform atk;
    private static Vector3 atkLP, atkCenter;
    private static Quaternion atkLR;
    private static bool atkMoved;
    private static Vector3 lastTip;
    private static bool lastTipOk, armed = true;
    private static float cooldownUntil, peak;
    private static int swings, tracked, untracked;
    private static bool loggedPose;
    private static SwingItem swing;
    private static Vector3 lastHand, heldPos;
    private static Quaternion heldRot = Quaternion.identity;
    private static float lastGoodAt = -10f;
    public static bool RealUseHeld;
    private static int glitches, buttonChops;
    private static float lastV;
    private static bool wasAttacking;
    private static float animStart = -10f, lastInjectAt = -10f;
    private static float attackSince = -1f;
    private static int stuckEnds;

    // wind back, chop through, recover (degrees added to the blade pitch; + is downward/forward)
    private static float ChopAngle(float t)
    {
        float arc = FPConfig.ButtonSwingDegrees;
        const float wind = 0.03f, strike = 0.08f, back = 0.10f;
        if (t < 0f || t > wind + strike + back) return 0f;
        if (t < wind) return -0.35f * arc * (t / wind);
        t -= wind;
        if (t < strike) { float k = t / strike; k = k * k * (3f - 2f * k); return Mathf.Lerp(-0.35f * arc, 0.65f * arc, k); }
        t -= strike;
        return Mathf.Lerp(0.65f * arc, 0f, t / back);
    }

    public static void Rebuild() { Unbuild(); item = null; }

    public static string Report()
    {
        string r = (built ? "in hand" : "not built") + ", tracked " + tracked + "/" + (tracked + untracked) + " frames"
            + (untracked > 0 && VRHand.Error.Length > 0 ? " (" + VRHand.Error + ")" : "")
            + ", peak tip speed " + peak.ToString("0.0") + " m/s (swing at " + FPConfig.SwingSpeed.ToString("0.0") + "), motion attacks " + swings
            + ", button attacks " + buttonChops + ", tracking glitches " + glitches
            + (stuckEnds > 0 ? ", STUCK attacks ended " + stuckEnds : "")
            + (swing != null ? ", weapon attacking now: " + swing.attack : "")
            + (Projectiles.Aimed + Projectiles.Other + Projectiles.Missed > 0 ? ", shots aimed " + Projectiles.Aimed
                + (Projectiles.Missed > 0 ? " (not found " + Projectiles.Missed + ")" : "") + ", other bullets " + Projectiles.Other : "");
        Projectiles.Aimed = Projectiles.Other = Projectiles.Missed = 0;
        peak = 0f; swings = tracked = untracked = glitches = buttonChops = stuckEnds = 0;
        return r;
    }

    public static bool Tick(GameObject held, Transform head, float worldScale)
    {
        Active = false;
        if (!FPConfig.Weapon6DOF || head == null || head.parent == null) { Hide(); return false; }
        if (held != item || builtScale != FPConfig.WeaponScale)
        {
            Unbuild();
            item = held;
            builtScale = FPConfig.WeaponScale;
            if (held != null)
            {
                try { Build(held); }
                catch (Exception e) { Unbuild(); Note = held.name + ": build failed " + e.GetType().Name + ": " + e.Message; Debug.Log("[FirstPersonLoD] VR sword: " + Note); }
            }
        }
        if (!built) { Hide(); return false; }

        Vector3 hp; Quaternion hr;
        if (VRHand.Get(FPConfig.WeaponHand != "left", out hp, out hr)) { tracked++; heldPos = hp; heldRot = hr; lastGoodAt = Time.realtimeSinceStartup; }
        else
        {
            untracked++;
            lastTipOk = false;
            // brief dropouts (hand out of the cameras' view mid-swing): keep the item where it was
            if (Time.realtimeSinceStartup - lastGoodAt > 1f) { Hide(); return true; }
            hp = heldPos; hr = heldRot;
        }
        Transform space = head.parent;
        if (!loggedPose)
        {
            loggedPose = true;
            Vector3 mp; Quaternion mr;
            bool ok = VRHand.Hmd(out mp, out mr);
            Debug.Log("[FirstPersonLoD] VR hand check: headset pose from compositor " + (ok ? mp.ToString("F3") : "n/a") + " vs head camera "
                + head.localPosition.ToString("F3") + " (should match); " + FPConfig.WeaponHand + " hand at " + hp.ToString("F3")
                + " rot " + hr.eulerAngles.ToString("F0"));
        }

        if (!anchor.activeSelf) anchor.SetActive(true);
        // attack started by the button (not by a physical swing): play a chop that pivots at the wrist
        bool attacking = swing != null && swing.attack;
        if (attacking && !wasAttacking && Time.realtimeSinceStartup - lastInjectAt > 0.3f && FPConfig.ButtonSwingDegrees > 0f)
            { animStart = Time.realtimeSinceStartup; buttonChops++; }
        wasAttacking = attacking;
        // safety net: an attack the game never ends (its animation stopped) blocks every later swing
        float nowT = Time.realtimeSinceStartup;
        if (!attacking) attackSince = -1f;
        else if (attackSince < 0f) attackSince = nowT;
        else if (!RealUseHeld && nowT - attackSince > FPConfig.AttackTimeout)
        {
            Renderer sr = swing.GetComponent<Renderer>();
            Debug.Log("[FirstPersonLoD] VR sword: attack on " + swing.name + " still running after " + (nowT - attackSince).ToString("0.0")
                + " s; ended it (renderer " + (sr == null ? "none" : "enabled " + sr.enabled + ", visible " + sr.isVisible
                + ", material " + (sr.sharedMaterial != null ? sr.sharedMaterial.name : "none")) + ")");
            swing.SendMessage("UnShot", SendMessageOptions.DontRequireReceiver);
            attackSince = -1f;
            stuckEnds++;
        }
        float chop = ChopAngle(Time.realtimeSinceStartup - animStart);

        Transform at = anchor.transform;
        at.position = space.TransformPoint(hp + hr * FPConfig.GripOffset);
        Quaternion oneHand = hr * Quaternion.Euler((isGun ? FPConfig.GunPitch : FPConfig.WeaponPitch) + (twoOn ? 0f : chop), 0f, 0f);
        at.rotation = space.rotation * TwoHand(oneHand, hp + hr * FPConfig.GripOffset);
        Active = true;
        BladeForward = at.forward;

        Vector3 tipW = at.TransformPoint(new Vector3(0f, 0f, bladeLen));
        TipWorld = tipW;
        Vector3 midW = at.TransformPoint(new Vector3(0f, 0f, bladeLen * 0.5f));
        if (atk != null)
        {
            atk.position = midW - atk.TransformVector(atkCenter);
            atkMoved = true;
        }

        // swing detection on the blade tip in tracking space (meters; unaffected by walking, turning, scale)
        Vector3 tipT = space.InverseTransformPoint(tipW);
        float dt = Time.unscaledDeltaTime;
        // a hand that jumps more than 25 cm in one frame is a tracking glitch, not a swing
        if (lastTipOk && (hp - lastHand).magnitude > 0.25f) { lastTipOk = false; glitches++; }
        lastHand = hp;
        if (lastTipOk && dt > 0.0005f)
        {
            float v1 = (tipT - lastTip).magnitude / dt;
            if (v1 > 40f) { glitches++; v1 = 0f; lastV = 0f; }   // no real swing is this fast: tracking noise
            float v = (v1 + lastV) * 0.5f;                        // two-frame average rejects one-frame flicks
            lastV = v1;
            if (v > peak) peak = v;
            // while the trigger is held the button owns the attack (hold = charge, release = strike)
            if (FPConfig.SwingSpeed > 0f && atk != null && !RealUseHeld && !attacking)
            {
                if (armed && v > FPConfig.SwingSpeed && Time.realtimeSinceStartup >= cooldownUntil && Inject == 0)
                {
                    Inject = 2;
                    lastInjectAt = Time.realtimeSinceStartup;
                    armed = false;
                    cooldownUntil = Time.realtimeSinceStartup + FPConfig.SwingCooldown;
                    swings++;
                }
                else if (!armed && v < FPConfig.SwingSpeed * 0.5f) armed = true;
            }
        }
        lastTip = tipT;
        lastTipOk = true;
        return true;
    }

    private static Mesh voxMesh;
    public static bool HasVisual;
    public static bool isGun;
    public static bool isFirearm;

    // ---- two hands on a gun -------------------------------------------------------------------
    // Put the free hand on the barrel (in front of the gun hand, close to the line the gun points
    // along) and the gun is held in both: it points from the gun hand to the free hand, rolled
    // with the gun hand, so it steadies like a real long gun. Take the free hand away (or pull the
    // hands together, or bend the gun hand well off the line) and the gun goes back to the gun
    // hand alone. Eased over a tenth of a second both ways. While both hands hold it, the free
    // hand's grip (drop) is ignored, so squeezing the barrel never throws the gun away.
    public static bool TwoHanded;
    private static bool twoOn;
    private static float twoBlend;
    private static Vector3 twoLast;
    private static int twoLogs;

    private static Quaternion TwoHand(Quaternion one, Vector3 grip)
    {
        TwoHanded = false;
        if (!FPConfig.TwoHandGuns || !isFirearm) { twoOn = false; twoBlend = 0f; return one; }
        bool mainRight = FPConfig.WeaponHand != "left";
        Vector3 lp; Quaternion lr;
        bool ok = VRHand.Get(!mainRight, out lp, out lr);
        Vector3 go = FPConfig.GripOffset; go.x = -go.x;
        Vector3 L = ok ? lp + lr * go : twoLast;
        Vector3 f = one * Vector3.forward;
        Vector3 rel = L - grip;
        float along = Vector3.Dot(rel, f);
        float off = (rel - f * along).magnitude;
        float dist = rel.magnitude;
        if (!ok) { if (twoOn) { twoOn = false; if (twoLogs++ < 20) Debug.Log("[FirstPersonLoD] VR two hands: let go (free hand lost tracking)"); } }
        else if (!twoOn)
        {
            if (along > FPConfig.TwoHandMin && along < FPConfig.TwoHandMax && off < FPConfig.TwoHandReach)
            {
                twoOn = true;
                if (twoLogs++ < 20) Debug.Log("[FirstPersonLoD] VR two hands: gun held in both hands (free hand " + (along * 100f).ToString("0") + " cm along the barrel, " + (off * 100f).ToString("0") + " cm off its line)");
            }
        }
        else
        {
            float ang = Vector3.Angle(rel, f);
            string why = dist < FPConfig.TwoHandMin * 0.5f ? "hands together" : dist > FPConfig.TwoHandMax + 0.15f ? "free hand away" : ang > 60f ? "gun hand turned off the line (" + ang.ToString("0") + " deg)" : null;
            if (why != null) { twoOn = false; if (twoLogs++ < 20) Debug.Log("[FirstPersonLoD] VR two hands: back to one hand (" + why + ")"); }
        }
        if (ok) twoLast = L;
        twoBlend = Mathf.MoveTowards(twoBlend, twoOn ? 1f : 0f, Time.unscaledDeltaTime / 0.1f);
        if (twoBlend <= 0f) return one;
        Vector3 d = twoLast - grip;
        if (d.sqrMagnitude < 1e-6f) return one;
        d.Normalize();
        Vector3 up = one * Vector3.up;
        up -= d * Vector3.Dot(up, d);
        if (up.sqrMagnitude < 1e-6f) up = one * Vector3.back;
        Quaternion two = Quaternion.LookRotation(d, up);
        TwoHanded = twoOn;
        float k = twoBlend * twoBlend * (3f - 2f * twoBlend);
        return Quaternion.Slerp(one, two, k);
    }

    public static Mesh BuildVoxels(bool[] solid, int fw, int fh, int fx, int fy, int W, int H, Vector2 off, Vector2 sc,
        float ax, float bx, float ay, float by, float sx, float sy)
    {
        // pixel edge -> item-local position (same mapping as the sprite quad)
        float[] qx = new float[fw + 1], qy = new float[fh + 1];
        for (int x = 0; x <= fw; x++) qx[x] = (ax * (((fx + x) / (float)W - off.x) / sc.x) + bx) * sx;
        for (int y = 0; y <= fh; y++) qy[y] = (ay * (((fy + y) / (float)H - off.y) / sc.y) + by) * sy;
        float d = Mathf.Min(Mathf.Abs(qx[1] - qx[0]), Mathf.Abs(qy[1] - qy[0])) * FPConfig.WeaponThickness;
        float z0 = -d * 0.5f, z1 = d * 0.5f;
        List<Vector3> v = new List<Vector3>(); List<Vector3> nr = new List<Vector3>();
        List<Vector2> uv = new List<Vector2>(); List<int> t = new List<int>();
        Vector3 xs = new Vector3(Mathf.Sign(qx[fw] - qx[0]), 0f, 0f), ys = new Vector3(0f, Mathf.Sign(qy[fh] - qy[0]), 0f);
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                if (!solid[y * fw + x]) continue;
                int tx = ((fx + x) % W + W) % W, ty = ((fy + y) % H + H) % H;
                Vector2 c = new Vector2((tx + 0.5f) / W, (ty + 0.5f) / H);
                float x0 = qx[x], x1 = qx[x + 1], y0 = qy[y], y1 = qy[y + 1];
                Quad(v, nr, uv, t, c, new Vector3(0f, 0f, -1f), new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0));
                Quad(v, nr, uv, t, c, new Vector3(0f, 0f, 1f), new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1));
                if (x == 0 || !solid[y * fw + x - 1]) Quad(v, nr, uv, t, c, -xs, new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1));
                if (x == fw - 1 || !solid[y * fw + x + 1]) Quad(v, nr, uv, t, c, xs, new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1));
                if (y == 0 || !solid[(y - 1) * fw + x]) Quad(v, nr, uv, t, c, -ys, new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1));
                if (y == fh - 1 || !solid[(y + 1) * fw + x]) Quad(v, nr, uv, t, c, ys, new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1));
            }
        Mesh m = new Mesh();
        m.name = "FPItemVoxels";
        m.SetVertices(v); m.SetNormals(nr); m.SetUVs(0, uv); m.SetTriangles(t, 0);
        m.RecalculateBounds();
        return m;
    }

    // one face, wound so its front faces along n (Unity: Cross(b - a, c - a) points out of the front)
    private static void Quad(List<Vector3> v, List<Vector3> nr, List<Vector2> uv, List<int> t, Vector2 c, Vector3 n,
        Vector3 a, Vector3 b, Vector3 cc, Vector3 d)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, cc - a), n) < 0f) { Vector3 tmp = b; b = d; d = tmp; }
        int i = v.Count;
        v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
        for (int k = 0; k < 4; k++) { nr.Add(n); uv.Add(c); }
        t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
    }

    private static void Hide()
    {
        Active = false;
        TwoHanded = false; twoOn = false; twoBlend = 0f;
        if (anchor != null && anchor.activeSelf) anchor.SetActive(false);
        RestoreAttackBox();
    }

    private static void RestoreAttackBox()
    {
        if (atk != null && atkMoved) { atk.localPosition = atkLP; atk.localRotation = atkLR; }
        atkMoved = false;
    }

    private static void Unbuild()
    {
        RestoreAttackBox();
        atk = null;
        swing = null;
        wasAttacking = false;
        if (anchor != null) UnityEngine.Object.Destroy(anchor);
        if (mat != null) UnityEngine.Object.Destroy(mat);
        if (voxMesh != null) UnityEngine.Object.Destroy(voxMesh);
        anchor = null; mat = null; voxMesh = null;
        HasVisual = false;
        built = false;
        Active = false;
        lastTipOk = false;
    }

    public static void Release()
    {
        Unbuild();
        item = null;
        Inject = 0;
        loggedPose = false;
    }

    // Held items sit a few centimetres from your eyes and can fill much of the view (the Knight's
    // shield is 1.4 m of art). The game's own item shaders (Transparent/Cutout/Bumped Specular and
    // Bumped Diffuse) run one full pass per pixel light, and first person forces up to 6 lights near
    // you to per-pixel plus the head light: that is up to 8 shading passes over a big patch of
    // screen, per eye. The blocks only exist where the sprite is opaque, so no alpha test is needed:
    // they get a single-pass vertex-lit material (all nearby lights, one pass) with the same texture.
    private static string swordMatNote = "";
    private static Shader cheap;
    private static bool cheapTried;

    public static Material HandMaterial(Material src, out string note)
    {
        if (FPConfig.HandItemShader == "cheap" && !cheapTried)
        {
            cheapTried = true;
            cheap = Shader.Find("Legacy Shaders/VertexLit") ?? Shader.Find("VertexLit") ?? Shader.Find("Mobile/VertexLit")
                ?? Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Diffuse");
            Debug.Log("[FirstPersonLoD] VR held items: " + (cheap != null ? "drawn with " + cheap.name + " (one pass)" : "no cheap shader in the game, using the item's own"));
        }
        Material m;
        if (FPConfig.HandItemShader == "cheap" && cheap != null)
        {
            m = new Material(cheap);
            m.mainTexture = src.mainTexture;
            if (m.HasProperty("_Color")) m.color = src.HasProperty("_Color") ? new Color(src.color.r, src.color.g, src.color.b, 1f) : Color.white;
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", Color.black);
            if (m.HasProperty("_Emission")) m.SetColor("_Emission", Color.black);
            note = "shader " + cheap.name + " (game's was " + (src.shader != null ? src.shader.name : "?") + ")";
        }
        else
        {
            m = new Material(src);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.1f);
            note = "shader " + (src.shader != null ? src.shader.name : "?") + " (game's own)";
        }
        m.mainTextureOffset = Vector2.zero;
        m.mainTextureScale = Vector2.one;
        return m;
    }

    public static void CheapRenderer(MeshRenderer mr)
    {
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
    }

    public static Color32[] ReadTexture(Texture tex)
    {
        RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D t2 = new Texture2D(tex.width, tex.height, TextureFormat.ARGB32, false);
        t2.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        t2.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        Color32[] px = t2.GetPixels32();
        UnityEngine.Object.Destroy(t2);
        return px;
    }

    private static void Build(GameObject held)
    {
        Renderer src = null; MeshFilter smf = null;
        MeshFilter[] mfs = held.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < mfs.Length; i++)
        {
            Renderer r = mfs[i].GetComponent<Renderer>();
            if (r == null || mfs[i].sharedMesh == null) continue;
            if (src == null || mfs[i].gameObject == held) { src = r; smf = mfs[i]; }
        }
        if (src == null) { Note = held.name + ": no mesh renderer"; Debug.Log("[FirstPersonLoD] VR sword: " + Note); return; }
        Material sm = FPDriver.OriginalMaterial(src); // the game's material even while hidden from your eyes
        Texture tex = sm != null ? sm.mainTexture : null;
        if (tex == null)
        {
            // fists and similar: nothing drawn, but the hit area follows your hand
            anchor = new GameObject("FPSwordInHand");
            UnityEngine.Object.DontDestroyOnLoad(anchor);
            anchor.SetActive(false);
            bladeLen = 0.15f * Mathf.Max(VRFP.WorldScale, 0.01f);
            built = true;
            BindAttackBox(held);
            Note = held.name + ": no texture (drawn by the character sprite), nothing shown in hand; attack box " + atkNote;
            Debug.Log("[FirstPersonLoD] VR sword: " + Note);
            return;
        }
        Mesh mesh = smf.sharedMesh;
        Vector2 off = sm.mainTextureOffset, sc = sm.mainTextureScale;
        int W = tex.width, H = tex.height;
        if (Mathf.Abs(sc.x) < 0.0001f || Mathf.Abs(sc.y) < 0.0001f) sc = Vector2.one;

        // mesh uv -> mesh local (quad: local = a*uv + b per axis)
        float ax = 1f, bx = -0.5f, ay = 1f, by = -0.5f;
        int tris = -1;
        string meshNote = "assumed unit quad";
        try
        {
            Vector3[] v = mesh.vertices; Vector2[] uv = mesh.uv;
            tris = mesh.triangles.Length / 3;
            int iu0 = 0, iu1 = 0, iv0 = 0, iv1 = 0;
            for (int i = 1; i < uv.Length; i++)
            {
                if (uv[i].x < uv[iu0].x) iu0 = i; if (uv[i].x > uv[iu1].x) iu1 = i;
                if (uv[i].y < uv[iv0].y) iv0 = i; if (uv[i].y > uv[iv1].y) iv1 = i;
            }
            if (uv.Length >= 3 && uv[iu1].x - uv[iu0].x > 0.0001f && uv[iv1].y - uv[iv0].y > 0.0001f)
            {
                ax = (v[iu1].x - v[iu0].x) / (uv[iu1].x - uv[iu0].x); bx = v[iu0].x - ax * uv[iu0].x;
                ay = (v[iv1].y - v[iv0].y) / (uv[iv1].y - uv[iv0].y); by = v[iv0].y - ay * uv[iv0].y;
                meshNote = v.Length + " verts " + tris + " tris, local x = " + ax.ToString("0.##") + "u" + (bx >= 0 ? "+" : "") + bx.ToString("0.##")
                    + ", y = " + ay.ToString("0.##") + "v" + (by >= 0 ? "+" : "") + by.ToString("0.##");
            }
        }
        catch (Exception) { meshNote = "mesh not readable, assumed unit quad"; }

        Vector3 ls = src.transform.lossyScale;
        float sx = Mathf.Abs(ls.x) * FPConfig.WeaponScale, sy = Mathf.Abs(ls.y) * FPConfig.WeaponScale;

        // the current frame's pixels (frame = tile at offset, size scale; continuous coordinates, wrapped on read)
        Color32[] px = ReadTexture(tex);
        float u0 = Mathf.Min(off.x, off.x + sc.x), v0 = Mathf.Min(off.y, off.y + sc.y);
        int fw = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(sc.x) * W)), fh = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(sc.y) * H));
        int fx = Mathf.RoundToInt(u0 * W), fy = Mathf.RoundToInt(v0 * H);
        double n = 0, mx = 0, my = 0;
        List<Vector2> pts = new List<Vector2>();
        bool[] solid = new bool[fw * fh];
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                int tx = ((fx + x) % W + W) % W, ty = ((fy + y) % H + H) % H;
                if (px[ty * W + tx].a < 128) continue;
                solid[y * fw + x] = true;
                float mu = ((fx + x + 0.5f) / W - off.x) / sc.x;   // mesh uv of this pixel
                float mv = ((fy + y + 0.5f) / H - off.y) / sc.y;
                Vector2 q = new Vector2((ax * mu + bx) * sx, (ay * mv + by) * sy);
                pts.Add(q); mx += q.x; my += q.y; n++;
            }

        Vector2 grip, tip;
        string how;
        // Guns and wands: the barrel direction is not guessed from the pixel shape. The game fires
        // along the character's front (local -X of the player, the side it faces), and the sprite
        // is drawn facing that way, so the muzzle points along the player's front seen in the
        // sprite's own plane, and the gun's top is the character's up. That keeps every gun
        // barrel-forward and right side up, whatever its art looks like.
        bool gunLike = held.GetComponentInChildren<Gun>() != null || held.GetComponentInChildren<MagicMissle>() != null;
        Vector2 gunDir = Vector2.zero, gunUp = Vector2.up;
        bool gunAxis = false;
        if (gunLike && n >= 3 && VRFP.Player != null)
        {
            Transform pt = VRFP.Player.transform;
            Vector3 L = src.transform.InverseTransformVector(pt.rotation * Vector3.left);
            Vector3 U = src.transform.InverseTransformVector(pt.up);
            gunDir = new Vector2(L.x * sx, L.y * sy);
            gunUp = new Vector2(U.x * sx, U.y * sy);
            if (gunDir.sqrMagnitude > 1e-10f) { gunDir.Normalize(); gunAxis = true; }
        }
        if (gunAxis)
        {
            mx /= n; my /= n;
            Vector2 m = new Vector2((float)mx, (float)my);
            float tmin = float.MaxValue, tmax = float.MinValue;
            for (int i = 0; i < pts.Count; i++) { float t = Vector2.Dot(pts[i] - m, gunDir); if (t < tmin) tmin = t; if (t > tmax) tmax = t; }
            float len = tmax - tmin;
            grip = m + gunDir * (tmin + len * 0.3f);   // hand a third of the way from the back
            tip = m + gunDir * tmax;                     // muzzle
            how = (int)n + " opaque px, gun: barrel along the character's front " + gunDir.ToString("F2") + ", top " + gunUp.normalized.ToString("F2");
        }
        else if (n >= 3)
        {
            mx /= n; my /= n;
            double cxx = 0, cxy = 0, cyy = 0;
            for (int i = 0; i < pts.Count; i++) { double dx = pts[i].x - mx, dy = pts[i].y - my; cxx += dx * dx; cxy += dx * dy; cyy += dy * dy; }
            double ang = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
            Vector2 a = new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang));
            Vector2 m = new Vector2((float)mx, (float)my);
            float tmin = float.MaxValue, tmax = float.MinValue;
            for (int i = 0; i < pts.Count; i++) { float t = Vector2.Dot(pts[i] - m, a); if (t < tmin) tmin = t; if (t > tmax) tmax = t; }
            Vector2 e0 = m + a * tmin, e1 = m + a * tmax;
            // the hilt end is the wide end (crossguard); the blade tapers to the tip
            Vector2 perp = new Vector2(-a.y, a.x);
            float len = tmax - tmin, w0lo = 0f, w0hi = 0f, w1lo = 0f, w1hi = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                float t = Vector2.Dot(pts[i] - m, a), w = Vector2.Dot(pts[i] - m, perp);
                if (t < tmin + len * 0.4f) { if (w < w0lo) w0lo = w; if (w > w0hi) w0hi = w; }
                if (t > tmax - len * 0.4f) { if (w < w1lo) w1lo = w; if (w > w1hi) w1hi = w; }
            }
            float wid0 = w0hi - w0lo, wid1 = w1hi - w1lo;
            // width right at each extreme end (outer 15%): a hammer or axe head is wide AT the end,
            // a sword's crossguard sits a little in from a narrow pommel
            float x0lo = 0f, x0hi = 0f, x1lo = 0f, x1hi = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                float t = Vector2.Dot(pts[i] - m, a), w = Vector2.Dot(pts[i] - m, perp);
                if (t < tmin + len * 0.15f) { if (w < x0lo) x0lo = w; if (w > x0hi) x0hi = w; }
                if (t > tmax - len * 0.15f) { if (w < x1lo) x1lo = w; if (w > x1hi) x1hi = w; }
            }
            float ext0 = x0hi - x0lo, ext1 = x1hi - x1lo;
            float pxw = Mathf.Min(Mathf.Abs(sx / fw), Mathf.Abs(sy / fh));
            Vector2 center = new Vector2((ax * 0.5f + bx) * sx, (ay * 0.5f + by) * sy);
            bool e0Grip;
            string gripHow;
            if (Mathf.Max(ext0, ext1) >= 2.5f * pxw && Mathf.Max(ext0, ext1) > 2.5f * Mathf.Min(ext0, ext1) + 0.0001f)
            { e0Grip = ext0 < ext1; gripHow = "heavy head at the far end (hammer/axe), held by the narrow end"; }
            else if (Mathf.Abs(wid0 - wid1) > 0.001f) { e0Grip = wid0 > wid1; gripHow = "wider end (crossguard)"; }
            else { e0Grip = (e0 - center).sqrMagnitude > (e1 - center).sqrMagnitude; gripHow = "same width, end away from frame centre"; }
            if (FPConfig.WeaponGripFlip) { e0Grip = !e0Grip; gripHow += ", flipped by config"; }
            Vector2 gEnd = e0Grip ? e0 : e1;
            tip = e0Grip ? e1 : e0;
            grip = gEnd + (tip - gEnd) * 0.12f;
            how = (int)n + " opaque px, axis " + (ang * 180.0 / Math.PI).ToString("0") + " deg, hilt = " + gripHow
                + " (end widths " + wid0.ToString("0.000") + " / " + wid1.ToString("0.000") + ")";
        }
        else
        {
            grip = new Vector2((ax * 0.5f + bx) * sx, (ay * 0.5f + by) * sy);
            tip = grip + new Vector2(0f, sy * 0.5f);
            how = "no opaque pixels found (" + (int)n + "), using frame centre";
        }
        Vector2 dir2 = tip - grip;
        bladeLen = dir2.magnitude;
        if (bladeLen < 0.001f) { dir2 = Vector2.up; bladeLen = sy * 0.5f; }
        dir2.Normalize();

        // mount: blade direction -> anchor +Z, grip -> anchor origin, sprite plane contains the blade
        Vector3 A = new Vector3(dir2.x, dir2.y, 0f), N = new Vector3(0f, 0f, 1f);
        Vector3 upIn = Vector3.Cross(A, N);
        if (gunAxis)
        {
            Vector3 u3 = new Vector3(gunUp.x, gunUp.y, 0f);
            u3 -= A * Vector3.Dot(u3, A);
            if (u3.sqrMagnitude > 1e-8f) upIn = u3.normalized;
        }
        Quaternion M = Quaternion.Inverse(Quaternion.LookRotation(A, upIn));
        Vector3 g3 = new Vector3(grip.x, grip.y, 0f);

        anchor = new GameObject("FPSwordInHand");
        UnityEngine.Object.DontDestroyOnLoad(anchor);
        mat = new Material(sm);
        float origCut;
        if (FPDriver.TryOriginalCutoff(sm, out origCut)) mat.SetFloat("_Cutoff", origCut);
        bool singleSided = tris >= 0 && tris <= 2;
        string shape;
        if (n >= 3 && FPConfig.WeaponVoxels)
        {
            // Minecraft-style: every opaque pixel becomes a block one pixel deep, coloured by sampling
            // that pixel's centre, so the item is solid from every angle
            UnityEngine.Object.Destroy(mat);
            mat = HandMaterial(sm, out swordMatNote);
            Mesh vox = BuildVoxels(solid, fw, fh, fx, fy, W, H, off, sc, ax, bx, ay, by, sx, sy);
            GameObject g = new GameObject("voxels");
            g.AddComponent<FPThickLayer>();
            g.layer = src.gameObject.layer;
            g.transform.SetParent(anchor.transform, false);
            g.transform.localRotation = M;
            g.transform.localPosition = -(M * g3);
            g.AddComponent<MeshFilter>().sharedMesh = vox;
            MeshRenderer mr = g.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            CheapRenderer(mr);
            voxMesh = vox;
            shape = "3D blocks (" + vox.vertexCount + " verts, " + swordMatNote + ")";
        }
        else
        {
            mat.mainTextureOffset = off;
            mat.mainTextureScale = sc;
            int planes = FPConfig.WeaponCross ? 2 : 1;
            for (int k = 0; k < planes; k++)
                for (int side = 0; side < (singleSided ? 2 : 1); side++)
                {
                    Quaternion R = Quaternion.AngleAxis(90f * k + 180f * side, Vector3.forward) * M;
                    GameObject g = new GameObject("plane" + k + (side == 1 ? "b" : ""));
                    g.AddComponent<FPThickLayer>();
                    g.layer = src.gameObject.layer;
                    g.transform.SetParent(anchor.transform, false);
                    g.transform.localRotation = R;
                    g.transform.localScale = new Vector3(sx, sy, 1f);
                    g.transform.localPosition = -(R * g3);
                    g.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer mr = g.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
            shape = "crossed flat planes";
        }
        HasVisual = true;
        how += ", drawn as " + shape;
        anchor.SetActive(false);
        built = true;

        BindAttackBox(held);
        isGun = held.GetComponentInChildren<Gun>() != null || held.GetComponentInChildren<MagicMissle>() != null;
        isFirearm = held.GetComponentInChildren<Gun>() != null;
        twoOn = false; twoBlend = 0f;
        {
            // everything that belongs to the item, to explain any extra shape seen near it
            StringBuilder parts = new StringBuilder();
            Renderer[] rs = held.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
                if (rs[i] != src) parts.Append("; " + rs[i].name + " " + rs[i].GetType().Name + (rs[i].enabled && rs[i].gameObject.activeInHierarchy ? "" : " (off)")
                    + " " + (rs[i].sharedMaterial != null && rs[i].sharedMaterial.shader != null ? rs[i].sharedMaterial.shader.name : "no material"));
            Light[] lts = held.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lts.Length; i++) parts.Append("; light " + lts[i].name + " " + lts[i].type + " range " + lts[i].range.ToString("0.0") + (lts[i].enabled ? "" : " (off)"));
            if (parts.Length > 0) Debug.Log("[FirstPersonLoD] VR held item " + held.name + " also has: " + parts.ToString().Substring(2));
        }
        float wsc = VRFPScale();
        Note = held.name + ": blade " + bladeLen.ToString("0.00") + " units" + (wsc > 0f ? " (" + (bladeLen / wsc).ToString("0.00") + " m)" : "")
            + ", " + how + ", frame " + fw + "x" + fh + " px at " + fx + "," + fy + " of " + W + "x" + H + ", " + meshNote
            + (singleSided ? ", single-sided (backs added)" : "") + ", shader " + (sm.shader != null ? sm.shader.name : "?") + "; attack box " + atkNote
            + (isGun ? "; shooter: held level with the controller (GunPitch " + FPConfig.GunPitch.ToString("0") + ")" : "");
        Debug.Log("[FirstPersonLoD] VR sword: " + Note);
    }

    private static string atkNote = "";

    private static void BindAttackBox(GameObject held)
    {
        atkNote = "none (not a swing weapon: no motion attacks)";
        swing = held.GetComponentInChildren<SwingItem>();
        SwingItem sw = swing;
        if (sw != null && sw.AttackBox != null)
        {
            atk = sw.AttackBox.transform;
            atkLP = atk.localPosition; atkLR = atk.localRotation;
            atkCenter = Vector3.zero;
            string shape = "?";
            Collider c = sw.AttackBox.GetComponent<Collider>();
            if (c is BoxCollider) { BoxCollider b = (BoxCollider)c; atkCenter = b.center; shape = "box " + atk.TransformVector(b.size).ToString("F2"); }
            else if (c is SphereCollider) { SphereCollider b = (SphereCollider)c; atkCenter = b.center; shape = "sphere r " + b.radius.ToString("0.00"); }
            else if (c is CapsuleCollider) { CapsuleCollider b = (CapsuleCollider)c; atkCenter = b.center; shape = "capsule h " + b.height.ToString("0.00"); }
            else if (c != null) shape = c.GetType().Name;
            atkNote = sw.AttackBox.name + " (" + shape + ", trigger " + (c != null && c.isTrigger) + ", parent " + (atk.parent != null ? atk.parent.name : "none")
                + ", local " + atkLP.ToString("F2") + ", attack length " + sw.attacklength.ToString("0.00") + ", lunge " + sw.thrustamt.ToString("0.00") + ")";
        }
    }

    private static float VRFPScale() { return VRFP.WorldScale; }
}

// ---------------------------------------------------------------------------------------------
// Weapon view. The held weapon is a flipbook quad parented to the player in the side-view plane,
// which in first person passes exactly through your eyes, so it is drawn edge-on (invisible). A
// proxy quad shares its mesh and material instance (Animate writes the frame offset into that
// instance, so the proxy plays the same idle/swing frames with no per-frame copying) and is held
// face-on in front of you at the weapon's own height and world size. Visual only: hits still come
// from the game's attack box, which AimWithHead turns toward where you look.
// ---------------------------------------------------------------------------------------------
public static class WeaponView
{
    private static GameObject proxy;
    private static MeshFilter pmf;
    private static MeshRenderer pmr;
    private static GameObject item;
    private static Renderer src;
    private static MeshFilter srcMf;
    public static string Note = "none";

    private static void Bind(GameObject held)
    {
        item = held;
        src = null; srcMf = null;
        if (held == null) return;
        MeshFilter[] mfs = held.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < mfs.Length; i++)
        {
            Renderer r = mfs[i].GetComponent<Renderer>();
            if (r == null || mfs[i].sharedMesh == null) continue;
            if (src == null || mfs[i].gameObject == held) { src = r; srcMf = mfs[i]; }
        }
        Note = held.name + (src != null
            ? "  quad " + src.name + " mesh " + srcMf.sharedMesh.bounds.size.ToString("F2") + " scale " + src.transform.lossyScale.ToString("F3")
              + " shader " + (src.sharedMaterial != null && src.sharedMaterial.shader != null ? src.sharedMaterial.shader.name : "?")
              + " offset from holder " + (src.transform.position - (held.transform.parent != null ? held.transform.parent.position : held.transform.position)).ToString("F3")
            : "  (no mesh renderer found)");
        Debug.Log("[FirstPersonLoD] VR weapon view: " + Note);
    }

    public static void Tick(GameObject held, Transform head, float worldScale)
    {
        if (held != item) Bind(held);
        bool show = FPConfig.WeaponView && src != null && head != null && src.enabled && src.gameObject.activeInHierarchy;
        if (!show) { if (pmr != null) pmr.enabled = false; return; }
        if (proxy == null)
        {
            proxy = new GameObject("FPWeaponView");
            UnityEngine.Object.DontDestroyOnLoad(proxy);
            proxy.AddComponent<FPThickLayer>(); // never picked up by the sprite scan
            pmf = proxy.AddComponent<MeshFilter>();
            pmr = proxy.AddComponent<MeshRenderer>();
            pmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pmr.receiveShadows = false;
        }
        proxy.layer = src.gameObject.layer;
        if (pmf.sharedMesh != srcMf.sharedMesh) pmf.sharedMesh = srcMf.sharedMesh;
        if (pmr.sharedMaterial != src.sharedMaterial) pmr.sharedMaterials = src.sharedMaterials;

        Vector3 fwd = head.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = head.up; // looking straight up/down
        fwd.y = 0f; fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 ls = src.transform.lossyScale;
        Vector3 size = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)) * FPConfig.WeaponScale;
        float w = Mathf.Max(size.x * srcMf.sharedMesh.bounds.size.x, 0.01f);
        Vector3 basePos = new Vector3(head.position.x, src.transform.position.y, head.position.z);
        proxy.transform.position = basePos + fwd * (w * FPConfig.WeaponDistance) + right * (w * FPConfig.WeaponRight) + Vector3.up * (w * FPConfig.WeaponUp);
        proxy.transform.rotation = Quaternion.LookRotation(fwd) * (FPConfig.WeaponMirror ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity);
        proxy.transform.localScale = size;
        pmr.enabled = true;
    }

    public static void Release()
    {
        if (pmr != null) pmr.enabled = false;
        item = null; src = null; srcMf = null;
    }
}

// ---------------------------------------------------------------------------------------------
// Projectiles. Guns and magic spawn their Bullet prefab at the weapon (plus FireOffset), parent it
// to the current room, and the game moves it along world X: facing exactly 0 flies -X, anything else
// +X. In first person the player faces your head direction, so shots flew one fixed way (and a
// bazooka rocket could fly straight back into you).
// Right after player 1's gun/magic fires, the new projectile is found (the room's child named after
// the weapon's Bullet prefab, nearest the weapon, not seen before), moved to the tip of the weapon
// in your hand, and given FPAimedProjectile, which keeps whatever speed the game gives it but locks
// its travel to where the weapon pointed when fired (up/down included). Everyone else's bullets are
// untouched except that their direction is fixed on their first frame (so sprite facing cannot
// swing them).
// ---------------------------------------------------------------------------------------------
public class FPAimedProjectile : MonoBehaviour
{
    public Vector3 dir = Vector3.forward;
    public Rigidbody rb;
    public BadGuyMovement seeker;        // the bazooka's rocket is a little creature that hunts targets
    public bool steering;                // flying where you aimed (a seeker lets go once it has a target)
    public float speed;                  // fastest speed its own logic has given it so far
    public bool pushed;                  // no Bullet script: the gun gave it one shove along the side-view axis
    private int fixedSteps;
    private Vector3 last;
    private bool started;

    private void Awake() { rb = GetComponent<Rigidbody>(); last = transform.position; }

    // Shots with the game's Bullet script are driven along your aim by the Bullet hook, and gravity
    // (where the game gives the shot gravity) bends them as it would in the side view. A shot the gun
    // only shoves once (flamethrower, flare gun) gets that shove turned onto your aim over its first
    // physics steps, and after that it flies on its own: arcs, drag and all.
    private void FixedUpdate()
    {
        if (seeker != null || rb == null || rb.isKinematic || !pushed) return;
        fixedSteps++;
        if (fixedSteps > 3) return;
        float sp = rb.velocity.magnitude;
        if (sp > 0.001f) rb.velocity = dir * sp;
    }

    private void LateUpdate()
    {
        // non-physics movers: keep the distance they moved this frame, along our direction
        if ((rb == null || rb.isKinematic) && seeker == null)
        {
            if (started)
            {
                float m = (transform.position - last).magnitude;
                if (m > 0.00001f) transform.position = last + dir * m;
            }
        }
        started = true;
        last = transform.position;
        if (seeker != null && !steering) return;
        // The game's shots are flat sprites made to be seen from the side (a beam is a long thin
        // quad). Keep the shot's length along its flight and turn its face toward your eyes, so a
        // beam looks like a beam from any angle instead of a dot or a sliver.
        Vector3 eye = Sprites.ViewerPos;
        Vector3 n = transform.position - eye;
        n -= dir * Vector3.Dot(n, dir);
        if (n.sqrMagnitude < 1e-6f) return;
        n.Normalize();
        transform.rotation = Quaternion.LookRotation(n, Vector3.Cross(n, -dir));
    }
}

public static class Projectiles
{
    private static readonly Dictionary<int, Vector3> dirs = new Dictionary<int, Vector3>();
    private static readonly Dictionary<int, bool> seenShots = new Dictionary<int, bool>();
    private static readonly Dictionary<int, FPAimedProjectile> seekers = new Dictionary<int, FPAimedProjectile>();
    private static readonly Dictionary<string, int> shotLogs = new Dictionary<string, int>();
    private static int seekerLogs;

    // BulletHurt reads the stats of whatever it touches: a "Player"/"BadGuy"-tagged part without
    // the character script (a hit box child) threw NullReferenceException in the game's own code,
    // which shots from your hand brush past more often than side-view shots. Those touches are skipped.
    private static int hurtSkips;
    public static bool BulletHurtPrefix(Collider __0)
    {
        try
        {
            if (__0 == null) return true;
            bool miss = (__0.CompareTag("Player") && __0.GetComponent<PlayerMovement2>() == null)
                || ((__0.CompareTag("BadGuy") || __0.CompareTag("GoodGuy")) && __0.GetComponent<BadGuyMovement>() == null);
            if (miss && hurtSkips++ < 5) Debug.Log("[FirstPersonLoD] VR shot touched " + __0.name + " (tag " + __0.tag + ", no character script): skipped instead of the game's null error");
            return !miss;
        }
        catch (Exception) { return true; }
    }

    // after a seeker's own FixedUpdate: keep its speed, point it along your aim until it has a target
    public static void SeekerFixedPostfix(BadGuyMovement __instance)
    {
        if (seekers.Count == 0 || __instance == null) return;
        FPAimedProjectile ap;
        int id = __instance.GetInstanceID();
        if (!seekers.TryGetValue(id, out ap)) return;
        try
        {
            if (ap == null || ap.rb == null) { seekers.Remove(id); return; }
            if (__instance.Target != null)
            {
                ap.steering = false;
                seekers.Remove(id);
                if (seekerLogs++ < 10) Debug.Log("[FirstPersonLoD] VR rocket " + __instance.name + " locked onto " + __instance.Target.name + " after flying your aim at " + ap.speed.ToString("0.0") + " units/s; its own homing takes over");
                return;
            }
            float sp = ap.rb.velocity.magnitude;
            if (sp > ap.speed) ap.speed = sp;
            float use = Mathf.Max(ap.speed, FPConfig.RocketMinSpeed);
            ap.rb.velocity = ap.dir * use;
        }
        catch (Exception) { seekers.Remove(id); }
    }
    public static int Aimed, Other, Missed;
    private static int logged;

    public static string Install()
    {
        BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        BindingFlags ps = BindingFlags.Public | BindingFlags.Static;
        Type me = typeof(Projectiles);
        string e1 = HarmonyShim.Prefix(typeof(Bullet).GetMethod("FixedUpdate", any), me.GetMethod("BulletFixedPrefix", ps));
        string e2 = HarmonyShim.Prefix(typeof(Bullet).GetMethod("Update", any), me.GetMethod("BulletUpdatePrefix", ps));
        string e3 = HarmonyShim.Postfix(typeof(Gun).GetMethod("Activate", any), me.GetMethod("GunFiredPostfix", ps));
        string e4 = HarmonyShim.Postfix(typeof(MagicMissle).GetMethod("Activate", any), me.GetMethod("MagicFiredPostfix", ps));
        string e5 = HarmonyShim.Postfix(typeof(BadGuyMovement).GetMethod("FixedUpdate", any), me.GetMethod("SeekerFixedPostfix", ps));
        string e6 = HarmonyShim.Prefix(typeof(BulletHurt).GetMethod("OnTriggerEnter", any), me.GetMethod("BulletHurtPrefix", ps));
        string err = e1 ?? e2 ?? e3 ?? e4 ?? e5 ?? e6;
        return err == null ? "shots aim from the weapon in hand" : "FAILED " + err;
    }

    public static void GunFiredPostfix(Gun __instance) { try { if (__instance != null) Fired(__instance, __instance.Bullet); } catch (Exception e) { Note(e); } }
    public static void MagicFiredPostfix(MagicMissle __instance) { try { if (__instance != null) Fired(__instance, __instance.Bullet); } catch (Exception e) { Note(e); } }

    private static void Note(Exception e) { if (logged++ < 10) Debug.Log("[FirstPersonLoD] VR shot aim failed: " + e.GetType().Name + ": " + e.Message); }

    private static void Fired(Component weapon, GameObject prefab)
    {
        GameObject p = VRFP.Player;
        if (!VRFP.Driving || !FPConfig.AimProjectiles || p == null || prefab == null || !weapon.transform.IsChildOf(p.transform)) return;
        CamOTron cot = Kami.Inst != null ? Kami.Inst.thecamotron : null;
        if (cot == null || cot.ThisRoom == null) return;
        string want = prefab.name + "(Clone)";
        Transform room = cot.ThisRoom.transform, best = null;
        float bestD = float.MaxValue;
        Vector3 wp = weapon.transform.position;
        for (int i = room.childCount - 1; i >= 0; i--)
        {
            Transform c = room.GetChild(i);
            if (c.name != want || seenShots.ContainsKey(c.GetInstanceID()) || c.GetComponent<FPAimedProjectile>() != null) continue;
            float d = (c.position - wp).sqrMagnitude;
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best == null || bestD > 400f)
        {
            Missed++;
            if (logged++ < 20)
            {
                ItemStat st = weapon.GetComponent<ItemStat>() ?? weapon.GetComponentInParent<ItemStat>();
                Gun g = weapon as Gun;
                MagicMissle mm = weapon as MagicMissle;
                bool inf = g != null ? g.infinate : mm != null && mm.infinate;
                Debug.Log("[FirstPersonLoD] VR shot from " + weapon.name + ": no new '" + want + "' in " + room.name
                    + " (ammo " + (st != null ? st.Ammo.ToString() : "?") + (inf ? ", infinite" : "") + (g != null && g.nobullet ? ", this gun fires no bullet" : "")
                    + (st != null && st.Ammo <= 0 && !inf ? ": OUT OF AMMO, the game only clicks" : "") + ")");
            }
            return;
        }
        if (bestD > 9f && logged++ < 20) Debug.Log("[FirstPersonLoD] VR shot from " + weapon.name + ": bullet found " + Mathf.Sqrt(bestD).ToString("0.0") + " units from the weapon");
        if (seenShots.Count > 2000) seenShots.Clear();
        seenShots[best.GetInstanceID()] = true;

        Vector3 d3 = Sword6.Active ? Sword6.BladeForward : VRFP.HeadForward;
        if (d3.sqrMagnitude < 0.0001f) d3 = Vector3.back;
        d3.Normalize();
        if (Sword6.Active) best.position = Sword6.TipWorld + d3 * 0.05f;
        best.rotation = Quaternion.FromToRotation(Vector3.left, d3);
        Rigidbody rb = best.GetComponent<Rigidbody>();
        string physNote = "";
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            // Side-view shots are pinned to their lane: beams (raygun, blaster) may only move along
            // the game's left/right axis (position Y and Z frozen), which is why they crawled sideways
            // no matter where you aimed. The lane pins come off; a beam that was pinned level also
            // flies level (no gravity); everything else keeps the game's gravity and drag.
            RigidbodyConstraints pos = RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezePositionZ;
            bool level = (rb.constraints & RigidbodyConstraints.FreezePositionY) != 0;
            if ((rb.constraints & pos) != 0) physNote = ", lane pins " + (rb.constraints & pos) + " removed";
            rb.constraints &= ~pos;
            if (level) rb.useGravity = false;
            physNote += ", gravity " + rb.useGravity + ", drag " + rb.drag.ToString("0.#");
        }
        FPAimedProjectile ap = best.gameObject.AddComponent<FPAimedProjectile>();
        ap.dir = d3;
        Bullet blt = best.GetComponent<Bullet>();
        ap.pushed = blt == null || blt.nophysics;
        BadGuyMovement seek = best.GetComponent<BadGuyMovement>();
        if (seek != null)
        {
            // the rocket runs its own hunting logic every physics step; let it, then point its speed
            // where you aimed until it has picked a target
            ap.seeker = seek; ap.steering = true;
            if (seekers.Count > 200) seekers.Clear();
            seekers[seek.GetInstanceID()] = ap;
        }
        Bullet bl = best.GetComponent<Bullet>();
        if (bl != null) dirs[bl.GetInstanceID()] = d3;
        Aimed++;
        int per; shotLogs.TryGetValue(weapon.name, out per);
        shotLogs[weapon.name] = per + 1;
        if (per < 2)
        {
            StringBuilder comps = new StringBuilder();
            Component[] cs = best.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++) if (cs[i] != null) comps.Append(" " + cs[i].GetType().Name);
            Debug.Log("[FirstPersonLoD] VR shot from " + weapon.name + ": " + best.name + " aimed " + d3.ToString("F2") + " from the weapon tip; components:" + comps
                + (rb != null ? " (rigidbody" + physNote + ")" : " (no rigidbody)") + (ap.seeker != null ? ", homing rocket: flies your aim until it locks on" : ap.pushed ? ", shoved once by the gun" : ", driven by its Bullet script"));
        }
    }

    // everyone else's bullets: the game's rule, evaluated once
    private static Vector3 Dir(Bullet b)
    {
        int id = b.GetInstanceID();
        Vector3 d;
        if (dirs.TryGetValue(id, out d)) return d;
        if (dirs.Count > 2000) dirs.Clear();
        d = b.transform.rotation.eulerAngles.y == 0f ? Vector3.left : Vector3.right;
        dirs[id] = d;
        Other++;
        return d;
    }

    public static bool BulletFixedPrefix(Bullet __instance)
    {
        try
        {
            Vector3 d = Dir(__instance);
            if (__instance.nophysics) return false;
            Rigidbody rb = __instance.GetComponent<Rigidbody>();
            if (rb == null) return true;
            rb.AddForce(d * __instance.speed, ForceMode.VelocityChange);
            return false;
        }
        catch (Exception) { return true; }
    }

    public static bool BulletUpdatePrefix(Bullet __instance)
    {
        try
        {
            Vector3 d = Dir(__instance);
            if (!__instance.nophysics) return false;
            __instance.transform.position += d * __instance.speed;
            return false;
        }
        catch (Exception) { return true; }
    }
}

// ---------------------------------------------------------------------------------------------
// Debug: give player 1 every item in the game (F2). Loads every pickup prefab from Resources/Stuff
// and runs the game's own pickup sequence for each: instantiate the pickup's RealItem, copy the
// pickup's stats onto it (its MoveStats, which is also how the lantern pickup flags the lantern),
// deactivate it and send ItemGet to the player, exactly as PickupItem does when you walk over one.
// Items you already carry are skipped (stackables just add one).

// ---------------------------------------------------------------------------------------------
// SaveGuard. The game's Save & Exit (Kami.SaveIt) writes every object tagged "Player". A player
// who dies is retagged "Ghost", so Save & Exit after the only player has died writes a save with
// nobody in it. Play then loads that save (Kami.LoadIt), switches off the Tavern's player, has no
// saved player to switch back on, and stays on its black loading screen with nothing to follow.
// Seen on 2026-09-26: the 2026-09-25 session died in the pause menu on floor 7 room 44 and saved;
// the next launch logged "Found Players" followed directly by "Inventory Loaded", then black.
// The game deletes save.txt as it loads it, so each bad save strands one launch.
//   SaveIt prefix:   no living player -> the save is not written (that run is over anyway)
//   DoLoadIt prefix: a save listing no players is moved to save_noplayers.txt and a new run is
//                    generated instead, exactly what the game does when there is no save
// Both are logged. Names are looked up plain first, then as renamed by the beta's obfuscator.
// ---------------------------------------------------------------------------------------------
public static class SaveGuard
{
    private const string Key = "+~r-zBm8,sW%_cFMPLm@N&L{w&6KVHJ4d8+fxYy!mt3%v.jZsvGV"; // the game's own save key
    private static MethodInfo mDecrypt, mGenerate;

    private static MethodInfo Find(Type t, string plain, string obf, BindingFlags f, Type[] args)
    {
        return t.GetMethod(plain, f, null, args, null) ?? t.GetMethod(obf, f, null, args, null);
    }

    public static string Install()
    {
        if (!FPConfig.SaveGuard) return "off (SaveGuard=false)";
        try
        {
            BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            BindingFlags ps = BindingFlags.Public | BindingFlags.Static;
            Type[] go = new Type[] { typeof(GameObject) };
            MethodInfo save = Find(typeof(Kami), "SaveIt", "AAAAAAAAAAAAAAAAAAAAhr", inst, Type.EmptyTypes);
            MethodInfo load = Find(typeof(Kami), "DoLoadIt", "AAAAAAAAAAAAAAAAAAAAAhh", inst, go);
            mGenerate = Find(typeof(Kami), "DoGenerate", "AAAAAAAAAAAAAAAAAAAgm", inst, go);
            mDecrypt = Find(typeof(Kami), "Decrypt", "AAAAAAAAAAAAAAAAAAAAAhg", stat, new Type[] { typeof(string), typeof(string) });
            string e1 = save == null ? "SaveIt not found" : HarmonyShim.Prefix(save, typeof(SaveGuard).GetMethod("SavePrefix", ps));
            string e2 = (load == null || mGenerate == null || mDecrypt == null)
                ? "load check not possible (" + (load == null ? "DoLoadIt " : "") + (mGenerate == null ? "DoGenerate " : "") + (mDecrypt == null ? "Decrypt" : "") + "missing)"
                : HarmonyShim.Prefix(load, typeof(SaveGuard).GetMethod("LoadPrefix", ps));
            return (e1 == null ? "save check applied" : "save check FAILED: " + e1) + ", " + (e2 == null ? "load check applied" : "load check FAILED: " + e2);
        }
        catch (Exception e) { return "FAILED: " + e.GetType().Name + ": " + e.Message; }
    }

    public static bool SavePrefix()
    {
        try
        {
            GameObject[] players = GameObject.FindGameObjectsWithTag("Player");
            int ghosts = 0;
            try { ghosts = GameObject.FindGameObjectsWithTag("Ghost").Length; } catch (Exception) { }
            if (players != null && players.Length > 0)
            {
                Debug.Log("[FirstPersonLoD] save guard: saving " + players.Length + " player(s)" + (ghosts > 0 ? " (" + ghosts + " ghost(s) not saved, as the game does)" : ""));
                return true;
            }
            Debug.Log("[FirstPersonLoD] save guard: Save & Exit with no living player (" + ghosts + " ghost(s)). The game would write a save with nobody in it,"
                + " which loads to a black screen. Not saved; the next Play starts a new run.");
            return false;
        }
        catch (Exception e)
        {
            Debug.Log("[FirstPersonLoD] save guard: check failed (" + e.GetType().Name + ": " + e.Message + "); the game saves as usual");
            return true;
        }
    }

    public static bool LoadPrefix(Kami __instance, GameObject __0)
    {
        string path = null;
        try
        {
            path = Application.dataPath + "/save.txt";
            if (!File.Exists(path)) return true;
            string text = mDecrypt.Invoke(null, new object[] { File.ReadAllText(path), Key }) as string;
            if (string.IsNullOrEmpty(text)) return true;   // the game treats an empty save as a new game itself
            string[] lines = text.Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
            // the game reads lines from index 10 on; after the line "Players" every non-empty line is a player
            int players = 0;
            bool inPlayers = false, foundSection = false;
            for (int i = 10; i < lines.Length; i++)
            {
                if (!inPlayers) { if (lines[i] == "Players") { inPlayers = true; foundSection = true; } continue; }
                if (lines[i].Length > 0) players++;
            }
            if (players > 0)
            {
                Debug.Log("[FirstPersonLoD] save guard: loading a save with " + players + " player(s), " + lines.Length + " lines");
                return true;
            }
            string keep = Application.dataPath + "/save_noplayers.txt";
            string moved;
            try { if (File.Exists(keep)) File.Delete(keep); File.Move(path, keep); moved = "moved to save_noplayers.txt"; }
            catch (Exception) { File.Delete(path); moved = "deleted (could not be moved)"; }
            Debug.Log("[FirstPersonLoD] save guard: the save lists no players (" + (foundSection ? "the Players section is empty" : "no Players section")
                + ", " + lines.Length + " lines; written by Save & Exit after the only player died). The game would load it to a black screen with nobody in it."
                + " Save " + moved + "; starting a new run instead.");
            mGenerate.Invoke(__instance, new object[] { __0 });
            return false;
        }
        catch (Exception e)
        {
            Debug.Log("[FirstPersonLoD] save guard: load check failed (" + e.GetType().Name + ": " + e.Message + ")"
                + (path != null && File.Exists(path) ? "; the game loads the save as usual" : "; the save is gone, starting a new run"));
            if (path != null && !File.Exists(path) && mGenerate != null)
            {
                try { mGenerate.Invoke(__instance, new object[] { __0 }); return false; } catch (Exception) { }
            }
            return true;
        }
    }
}
// ---------------------------------------------------------------------------------------------
public static class GiveAll
{
    // Items the game treats as progress, not equipment: carrying the Golden Cat wins the run and is
    // written to your Steam stats ("Obtained the Golden Cat"), a guitar unlocks a class when you
    // walk past its unlock spot, and the crystal counts as the floor's treasure at the stairs.
    private static bool Excluded(string name)
    {
        string n = name.ToLowerInvariant();
        string[] skip = FPConfig.GiveAllSkip.ToLowerInvariant().Split(',');
        for (int i = 0; i < skip.Length; i++) { string k = skip[i].Trim(); if (k.Length > 0 && n.Contains(k)) return true; }
        return false;
    }

    public static string Run(GameObject player)
    {
        if (player == null) return "no player";
        Inventory inv = player.GetComponentInChildren<Inventory>();
        if (inv == null) return "player has no inventory";
        UnityEngine.Object[] all = Resources.LoadAll("Stuff", typeof(GameObject));
        Dictionary<string, bool> have = new Dictionary<string, bool>();
        if (inv.Stuff != null)
            for (int i = 0; i < inv.Stuff.Count; i++)
                if (inv.Stuff[i] != null) have[inv.Stuff[i].name.Replace("(Clone)", "")] = true;
        int given = 0, skipped = 0, failed = 0, ammoFilled = 0;
        StringBuilder names = new StringBuilder();
        StringBuilder excluded = new StringBuilder();
        for (int i = 0; i < all.Length; i++)
        {
            GameObject prefab = all[i] as GameObject;
            if (prefab == null) continue;
            PickupItem pi = prefab.GetComponent<PickupItem>();
            if (pi == null || pi.RealItem == null) continue;
            string nm = pi.RealItem.name;
            if (Excluded(nm + " " + prefab.name)) { excluded.Append(" " + nm); continue; }
            ItemStat rs = pi.RealItem.GetComponent<ItemStat>();
            bool stack = rs != null && rs.stackable;
            if (have.ContainsKey(nm) && !stack) { skipped++; continue; }
            try
            {
                GameObject real = (GameObject)UnityEngine.Object.Instantiate(pi.RealItem);
                MonoBehaviour[] mbs = prefab.GetComponents<MonoBehaviour>();
                for (int k = 0; k < mbs.Length; k++)
                {
                    if (mbs[k] == null) continue;
                    MethodInfo ms = mbs[k].GetType().GetMethod("MoveStats", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new Type[] { typeof(GameObject) }, null);
                    if (ms != null) { ms.Invoke(mbs[k], new object[] { real }); continue; }
                    ms = mbs[k].GetType().GetMethod("MoveStats", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (ms != null) ms.Invoke(mbs[k], null);
                }
                if (real.GetComponent<ItemStat>() == null) real.AddComponent<ItemStat>();
                // debug items come straight from the prefab, without the ammo a floor pickup rolls:
                // fill shooters so every gun and wand can be tried
                if (real.GetComponentsInChildren<Gun>(true).Length > 0 || real.GetComponentsInChildren<MagicMissle>(true).Length > 0)
                {
                    ItemStat st = real.GetComponent<ItemStat>();
                    if (st.Ammo < FPConfig.GiveAllAmmo) { st.Ammo = FPConfig.GiveAllAmmo; ammoFilled++; }
                }
                real.SetActive(false);
                player.SendMessage("ItemGet", real);
                have[nm] = true;
                given++;
                if (names.Length < 1500) names.Append(" " + nm);
            }
            catch (Exception e)
            {
                failed++;
                Debug.Log("[FirstPersonLoD] give-all: " + prefab.name + " failed: " + e.GetType().Name + ": " + e.Message);
            }
        }
        // shooters already carried get a refill too
        if (inv.Stuff != null)
            for (int i = 0; i < inv.Stuff.Count; i++)
            {
                GameObject it = inv.Stuff[i];
                if (it == null || (it.GetComponentsInChildren<Gun>(true).Length == 0 && it.GetComponentsInChildren<MagicMissle>(true).Length == 0)) continue;
                ItemStat st = it.GetComponent<ItemStat>();
                if (st != null && st.Ammo < FPConfig.GiveAllAmmo) { st.Ammo = FPConfig.GiveAllAmmo; ammoFilled++; }
            }
        // the weapon in your hand is its own copy of the carried item (the game copies its stats back
        // when you switch): refill that one too, whatever it has left
        int heldFilled = 0;
        List<ItemStat> held = new List<ItemStat>();
        Gun[] guns = player.GetComponentsInChildren<Gun>(true);
        for (int i = 0; i < guns.Length; i++) { ItemStat st = guns[i].GetComponent<ItemStat>() ?? guns[i].GetComponentInParent<ItemStat>(); if (st != null && !held.Contains(st)) held.Add(st); }
        MagicMissle[] wands = player.GetComponentsInChildren<MagicMissle>(true);
        for (int i = 0; i < wands.Length; i++) { ItemStat st = wands[i].GetComponent<ItemStat>() ?? wands[i].GetComponentInParent<ItemStat>(); if (st != null && !held.Contains(st)) held.Add(st); }
        StringBuilder heldNames = new StringBuilder();
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i].Ammo >= FPConfig.GiveAllAmmo) continue;
            heldNames.Append(" " + held[i].name + " " + held[i].Ammo + "->" + FPConfig.GiveAllAmmo);
            held[i].Ammo = FPConfig.GiveAllAmmo;
            heldFilled++;
        }
        string r = "gave " + given + " items (" + skipped + " already carried" + (failed > 0 ? ", " + failed + " failed" : "") + ") from " + all.Length
            + " in Resources/Stuff; " + ammoFilled + " shooters filled to " + FPConfig.GiveAllAmmo + " ammo"
            + (heldFilled > 0 ? "; in your hand:" + heldNames : "");
        if (excluded.Length > 0) r += "; left out (they end the run or unlock things for real):" + excluded;
        Debug.Log("[FirstPersonLoD] give-all: " + r + ":" + names);
        return r;
    }
}

// ---------------------------------------------------------------------------------------------
// SteamVR input setup owned by the mod. Written on every launch in plugin Awake, which runs before
// the game's SteamVR plugin registers LegendofDungeon_Data/StreamingAssets/SteamVR/actions.json,
// so SteamVR always sees the current layout. Originals are kept once as *.orig. Adds snap-turn
// actions and default bindings for Index (knuckles, also what Steam Link reports for Quest) and
// Oculus Touch (Meta Link runtime). Config VRBindings=false leaves the game's files alone.
// ---------------------------------------------------------------------------------------------
public static class VRBindings
{
    private const string KnucklesSources = @"[
  {
    ""inputs"": {
      ""position"": {
        ""output"": ""/actions/vr_controls/in/move""
      },
      ""click"": {
        ""output"": ""/actions/vr_controls/in/menu""
      }
    },
    ""mode"": ""joystick"",
    ""path"": ""/user/hand/left/input/thumbstick""
  },
  {
    ""inputs"": {
      ""east"": {
        ""output"": ""/actions/vr_controls/in/turnright""
      },
      ""west"": {
        ""output"": ""/actions/vr_controls/in/turnleft""
      }
    },
    ""mode"": ""dpad"",
    ""parameters"": {
      ""sub_mode"": ""touch""
    },
    ""path"": ""/user/hand/right/input/thumbstick""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/use""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/trigger""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/next""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/b""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/jump""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/a""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/drop""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/grip""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/hotkey1""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/b""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/hotkey2""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/a""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/prev""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/trigger""
  }
]";
    // Quest / Rift (Oculus Touch): the game's own layout (left stick move, click = pause, right stick
    // snap turn, right trigger use, A jump, B next item, left grip drop, Y / X hotkeys, left trigger
    // previous item or a switch in the free hand's reach) plus what the Frame has on buttons Touch
    // lacks: right grip (held) = bare hands (held again = back), right stick click (held) =
    // inventory screen. The stick click only pauses when held half a second with the stick centred
    // (VRFP.MenuGuard), so a hard push while running cannot pause the game.
    private const string TouchSources = @"[
  {
    ""inputs"": {
      ""position"": {
        ""output"": ""/actions/vr_controls/in/move""
      },
      ""click"": {
        ""output"": ""/actions/vr_controls/in/menu""
      }
    },
    ""mode"": ""joystick"",
    ""path"": ""/user/hand/left/input/joystick""
  },
  {
    ""inputs"": {
      ""east"": {
        ""output"": ""/actions/vr_controls/in/turnright""
      },
      ""west"": {
        ""output"": ""/actions/vr_controls/in/turnleft""
      }
    },
    ""mode"": ""dpad"",
    ""parameters"": {
      ""sub_mode"": ""touch""
    },
    ""path"": ""/user/hand/right/input/joystick""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/use""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/trigger""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/next""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/b""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/jump""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/a""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/drop""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/grip""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/hotkey1""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/y""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/hotkey2""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/x""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/prev""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/trigger""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/hands""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/grip""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/inventory""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/joystick""
  }
]";
    // Steam Frame controllers, native layout (SteamVR controller type "frame_controller"; input
    // paths from Valve's Steam Frame input documentation). Without a binding for this type SteamVR
    // runs the game on its Oculus Touch binding, where the Frame's D-pad left, up and right all
    // arrive as the Touch Y button, so left and right cannot be told apart. Same layout as Touch
    // for everything the Touch binding has, plus the left D-pad for the inventory:
    // left/right = previous/next item, up/down = hotkey A/B (through the mod's own D-pad actions, so
    // the inventory screen can tell the D-pad from other buttons); left View button (held) =
    // recenter; left bumper (held) = inventory screen.
    // Right X and Y also give next item, as they did under the Touch emulation. Pause is the right
    // Menu button (v0.9.7: it was the left stick click, which a hard push while running pressed by
    // accident and dropped you in the pause menu).
    private const string FrameSources = @"[
  {
    ""inputs"": {
      ""position"": {
        ""output"": ""/actions/vr_controls/in/move""
      }
    },
    ""mode"": ""joystick"",
    ""path"": ""/user/hand/left/input/thumbstick""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/menu""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/menu""
  },
  {
    ""inputs"": {
      ""east"": {
        ""output"": ""/actions/vr_controls/in/turnright""
      },
      ""west"": {
        ""output"": ""/actions/vr_controls/in/turnleft""
      }
    },
    ""mode"": ""dpad"",
    ""parameters"": {
      ""sub_mode"": ""touch""
    },
    ""path"": ""/user/hand/right/input/thumbstick""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/use""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/trigger""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/jump""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/a""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/next""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/b""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/next""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/x""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/next""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/y""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/drop""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/grip""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/prev""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/trigger""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/invleft""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/dpad_left""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/invright""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/dpad_right""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/invup""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/dpad_up""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/invdown""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/dpad_down""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/recenter""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/view""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/inventory""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/left/input/bumper""
  },
  {
    ""inputs"": {
      ""click"": {
        ""output"": ""/actions/vr_controls/in/hands""
      }
    },
    ""mode"": ""button"",
    ""path"": ""/user/hand/right/input/bumper""
  }
]";
    private const string FrameDefault = @"{""controller_type"": ""frame_controller"", ""binding_url"": ""bindings_frame_controller.json""}";
    private const string RecenterAction = @"{""name"": ""/actions/VR_Controls/in/Recenter"", ""type"": ""boolean"", ""requirement"": ""optional""}";
    private const string InvActions = @"{""name"": ""/actions/VR_Controls/in/InvLeft"", ""type"": ""boolean"", ""requirement"": ""optional""}, {""name"": ""/actions/VR_Controls/in/InvRight"", ""type"": ""boolean"", ""requirement"": ""optional""}, {""name"": ""/actions/VR_Controls/in/InvUp"", ""type"": ""boolean"", ""requirement"": ""optional""}, {""name"": ""/actions/VR_Controls/in/InvDown"", ""type"": ""boolean"", ""requirement"": ""optional""}, {""name"": ""/actions/VR_Controls/in/Inventory"", ""type"": ""boolean"", ""requirement"": ""optional""}, {""name"": ""/actions/VR_Controls/in/Hands"", ""type"": ""boolean"", ""requirement"": ""optional""}";
    private const string TurnActions = @"{""name"": ""/actions/VR_Controls/in/TurnLeft"", ""type"": ""boolean"", ""requirement"": ""optional""}, {""name"": ""/actions/VR_Controls/in/TurnRight"", ""type"": ""boolean"", ""requirement"": ""optional""}";
    private const string TouchDefault = @"{""controller_type"": ""oculus_touch"", ""binding_url"": ""bindings_oculus_touch.json""}";

    // Edits the game's own files starting from the untouched originals (*.orig), so everything the
    // mod does not know about (e.g. the "Controller Position" pose action and its pose bindings)
    // is preserved. Only three things change: two snap-turn actions and a Touch default binding are
    // inserted into the manifest, and the "sources" button list is replaced in the controller files.
    public static void Ensure()
    {
        if (!FPConfig.VRBindings) { Debug.Log("[FirstPersonLoD] VR bindings: left untouched (VRBindings=false)"); return; }
        try
        {
            string dir = Path.Combine(Path.Combine(Application.dataPath, "StreamingAssets"), "SteamVR");
            if (!Directory.Exists(dir)) { Debug.Log("[FirstPersonLoD] VR bindings: " + dir + " not found (not the beta build?)"); return; }
            string manP = Path.Combine(dir, "actions.json");
            string knP = Path.Combine(dir, "bindings_knuckles.json");
            string toP = Path.Combine(dir, "bindings_oculus_touch.json");
            string frP = Path.Combine(dir, "bindings_frame_controller.json");
            string manBase = ReadOriginal(manP);
            string knBase = ReadOriginal(knP);
            if (manBase == null || knBase == null) { Debug.LogError("[FirstPersonLoD] VR bindings: game files missing; nothing changed"); return; }

            string m = manBase;
            if (m.IndexOf("/actions/VR_Controls/in/TurnLeft") < 0) m = InsertIntoArray(m, "\"actions\"", TurnActions);
            if (m.IndexOf("oculus_touch") < 0) m = InsertIntoArray(m, "\"default_bindings\"", TouchDefault);
            if (m.IndexOf("/actions/VR_Controls/in/Recenter") < 0) m = InsertIntoArray(m, "\"actions\"", RecenterAction);
            if (m.IndexOf("/actions/VR_Controls/in/Inventory") < 0) m = InsertIntoArray(m, "\"actions\"", InvActions);   // includes Hands
            if (m.IndexOf("frame_controller") < 0) m = InsertIntoArray(m, "\"default_bindings\"", FrameDefault);

            // every action path in the original must survive
            int kept = 0, lost = 0;
            StringBuilder lostNames = new StringBuilder();
            foreach (System.Text.RegularExpressions.Match mt in System.Text.RegularExpressions.Regex.Matches(manBase, "\"(/actions/[^\"]+)\""))
            {
                if (m.IndexOf(mt.Groups[1].Value) >= 0) kept++;
                else { lost++; lostNames.Append(" " + mt.Groups[1].Value); }
            }
            if (lost > 0) { Debug.LogError("[FirstPersonLoD] VR bindings: edit would drop" + lostNames + "; nothing changed"); return; }

            string k = ReplaceArray(knBase, "\"sources\"", KnucklesSources);
            string t = ReplaceArray(knBase, "\"sources\"", TouchSources).Replace("\"knuckles\"", "\"oculus_touch\"");
            string f = ReplaceArray(knBase, "\"sources\"", FrameSources).Replace("\"knuckles\"", "\"frame_controller\"");

            Debug.Log("[FirstPersonLoD] VR bindings: " + Put(manP, m) + ", " + Put(knP, k) + ", " + Put(toP, t) + ", " + Put(frP, f)
                + "  (" + kept + " original action references kept)");
        }
        catch (Exception e) { Debug.LogError("[FirstPersonLoD] VR bindings failed, nothing changed: " + e.Message); }
    }

    // the untouched original: *.orig if a backup exists, otherwise back up the current file first
    private static string ReadOriginal(string p)
    {
        if (File.Exists(p + ".orig")) return File.ReadAllText(p + ".orig");
        if (!File.Exists(p)) return null;
        File.Copy(p, p + ".orig");
        return File.ReadAllText(p);
    }

    private static string Put(string p, string content)
    {
        string name = Path.GetFileName(p);
        if (File.Exists(p) && File.ReadAllText(p) == content) return name + " up to date";
        File.WriteAllText(p, content);
        return name + " written";
    }

    // index of the ']' matching the '[' at open, honoring JSON strings
    private static int MatchBracket(string s, int open)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) return i; }
        }
        throw new Exception("unbalanced JSON array");
    }

    private static int ArrayStart(string s, string key)
    {
        int k = s.IndexOf(key);
        if (k < 0) throw new Exception(key + " not found");
        int b = s.IndexOf('[', k);
        if (b < 0) throw new Exception(key + " has no array");
        return b;
    }

    private static string InsertIntoArray(string s, string key, string item)
    {
        int b = ArrayStart(s, key);
        int e = MatchBracket(s, b);
        bool empty = s.Substring(b + 1, e - b - 1).Trim().Length == 0;
        return s.Substring(0, b + 1) + "\n" + item + (empty ? "\n" : ",\n") + s.Substring(b + 1);
    }

    private static string ReplaceArray(string s, string key, string arr)
    {
        int b = ArrayStart(s, key);
        int e = MatchBracket(s, b);
        return s.Substring(0, b) + arr + s.Substring(e + 1);
    }
}

// Harmony by reflection (no compile-time dependency on a specific 0Harmony build).
public static class HarmonyShim
{
    private static object harmony;
    private static MethodInfo patchMi;
    private static ConstructorInfo hmCtor;

    private static string Ready()
    {
        if (harmony != null) return null;
        Assembly h = null;
        Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < asms.Length; i++)
            if (asms[i].GetType("HarmonyLib.Harmony", false) != null) { h = asms[i]; break; }
        if (h == null) return "HarmonyLib not found";
        hmCtor = h.GetType("HarmonyLib.HarmonyMethod").GetConstructor(new Type[] { typeof(MethodInfo) });
        object inst = Activator.CreateInstance(h.GetType("HarmonyLib.Harmony"), new object[] { "com.sampatek.lod.firstperson" });
        MethodInfo[] ms = inst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < ms.Length; i++)
        {
            if (ms[i].Name != "Patch") continue;
            ParameterInfo[] ps = ms[i].GetParameters();
            if (ps.Length >= 3 && ps[0].ParameterType == typeof(MethodBase)) { patchMi = ms[i]; break; }
        }
        if (patchMi == null || hmCtor == null) return "Harmony.Patch not found";
        harmony = inst;
        return null;
    }

    // returns null on success, otherwise a short error
    public static string Postfix(MethodBase target, MethodInfo postfix) { return Patch(target, postfix, 2); }
    public static string PrePost(MethodBase target, MethodInfo prefix, MethodInfo postfix)
    {
        try
        {
            string r = Ready();
            if (r != null) return r;
            object[] args = new object[patchMi.GetParameters().Length];
            args[0] = target;
            args[1] = hmCtor.Invoke(new object[] { prefix });
            args[2] = hmCtor.Invoke(new object[] { postfix });
            patchMi.Invoke(harmony, args);
            return null;
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }
    }
    private static MethodInfo unpatchMi;
    public static string Unpatch(MethodBase target, MethodInfo patch)
    {
        try
        {
            string r = Ready();
            if (r != null) return r;
            if (unpatchMi == null)
            {
                MethodInfo[] ms = harmony.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "Unpatch") continue;
                    ParameterInfo[] ps = ms[i].GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(MethodBase) && ps[1].ParameterType == typeof(MethodInfo)) { unpatchMi = ms[i]; break; }
                }
                if (unpatchMi == null) return "Harmony.Unpatch not found";
            }
            unpatchMi.Invoke(harmony, new object[] { target, patch });
            return null;
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }
    }
    public static string Prefix(MethodBase target, MethodInfo prefix) { return Patch(target, prefix, 1); }

    private static string Patch(MethodBase target, MethodInfo patch, int slot)
    {
        try
        {
            string r = Ready();
            if (r != null) return r;
            if (target == null || patch == null) return "patch target or " + (slot == 1 ? "prefix" : "postfix") + " not found";
            object[] args = new object[patchMi.GetParameters().Length];
            args[0] = target;
            args[slot] = hmCtor.Invoke(new object[] { patch });
            patchMi.Invoke(harmony, args);
            return null;
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return "FAILED: " + e.GetType().Name + ": " + e.Message;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Garbage census (GarbageProbe, once per session). The game itself makes 3-12 MB of garbage a
// second in the dungeon; every garbage collection is a hitch (the 20-45 ms worst frames). To find
// where it comes from, after a minute of first person in the dungeon every Update, LateUpdate,
// FixedUpdate and OnGUI of the game's scripts is wrapped for 10 seconds: memory in use is read
// before and after each call, and the growth is added to that method. The hooks go on and come
// off a few dozen methods per frame (no stall). Memory in use grows in blocks, so a single call
// can be over- or under-counted; over thousands of calls the totals show which scripts allocate.
// The log lists the top methods in KB per second with their call counts, and how much of all the
// garbage in that window they account for.
// ---------------------------------------------------------------------------------------------
public static class GarbageProbe
{
    private static readonly List<MethodBase> targets = new List<MethodBase>();
    private static readonly List<MethodBase> patched = new List<MethodBase>();
    private static readonly Dictionary<MethodBase, long> bytes = new Dictionary<MethodBase, long>();
    private static readonly Dictionary<MethodBase, int> calls = new Dictionary<MethodBase, int>();
    private static MethodInfo pre, post;
    private static int stage, idx, failed;   // 0 waiting, 1 patching, 2 measuring, 3 unpatching, 4 done
    private static float drivenFor, measureStart, measureEnd;
    private static int gcStart;
    public static bool Measuring;

    public static void Pre(out long __state) { __state = Measuring ? GC.GetTotalMemory(false) : 0; }
    public static void Post(MethodBase __originalMethod, long __state)
    {
        if (!Measuring || __state == 0) return;
        long d = GC.GetTotalMemory(false) - __state;
        if (d <= 0) return;   // a collection ran inside: skip
        long b; bytes.TryGetValue(__originalMethod, out b); bytes[__originalMethod] = b + d;
        int c; calls.TryGetValue(__originalMethod, out c); calls[__originalMethod] = c + 1;
    }

    private static void Find()
    {
        BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string[] names = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };
        Assembly[] asms = { typeof(Kami).Assembly, typeof(cInput).Assembly };
        for (int a = 0; a < asms.Length; a++)
        {
            Type[] ts;
            try { ts = asms[a].GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types; }
            for (int i = 0; i < ts.Length; i++)
            {
                Type t = ts[i];
                if (t == null || !typeof(MonoBehaviour).IsAssignableFrom(t) || t.ContainsGenericParameters) continue;
                for (int k = 0; k < names.Length; k++)
                {
                    MethodInfo m = t.GetMethod(names[k], f, null, Type.EmptyTypes, null);
                    if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                    targets.Add(m);
                }
            }
        }
    }

    // every frame, first person or not
    public static void Tick(bool driving, string room)
    {
        if (!FPConfig.GarbageProbe || stage == 4) return;
        try
        {
            if (stage == 0)
            {
                if (driving && room != null && room != "Tavern") drivenFor += Time.unscaledDeltaTime;
                if (drivenFor < 60f) return;
                pre = typeof(GarbageProbe).GetMethod("Pre");
                post = typeof(GarbageProbe).GetMethod("Post");
                Find();
                stage = 1; idx = 0;
                Debug.Log("[FirstPersonLoD] garbage census: wrapping " + targets.Count + " update methods of the game's scripts (a few dozen per frame)");
                return;
            }
            if (stage == 1)
            {
                for (int n = 0; n < 25 && idx < targets.Count; n++, idx++)
                {
                    if (HarmonyShim.PrePost(targets[idx], pre, post) != null) failed++;
                    else patched.Add(targets[idx]);
                }
                if (idx < targets.Count) return;
                stage = 2;
                bytes.Clear(); calls.Clear();
                measureStart = Time.realtimeSinceStartup;
                gcStart = GC.CollectionCount(0);
                Measuring = true;
                return;
            }
            if (stage == 2)
            {
                if (Time.realtimeSinceStartup - measureStart < 10f) return;
                Measuring = false;
                measureEnd = Time.realtimeSinceStartup;
                stage = 3; idx = 0;
                try { Report(); } catch (Exception e) { Debug.Log("[FirstPersonLoD] garbage census report failed: " + e.GetType().Name + ": " + e.Message); }
                return;
            }
            if (stage == 3)
            {
                for (int n = 0; n < 12 && idx < patched.Count; n++, idx++)
                {
                    HarmonyShim.Unpatch(patched[idx], pre);
                    HarmonyShim.Unpatch(patched[idx], post);
                }
                if (idx < patched.Count) return;
                stage = 4;
                Debug.Log("[FirstPersonLoD] garbage census: hooks removed");
            }
        }
        catch (Exception e)
        {
            Measuring = false;
            Debug.Log("[FirstPersonLoD] garbage census failed (" + e.GetType().Name + ": " + e.Message + ")" + (stage < 3 ? "; removing its hooks" : ""));
            if (stage < 3) { stage = 3; idx = 0; } else stage = 4;
        }
    }

    private static void Report()
    {
        float secs = Mathf.Max(0.1f, measureEnd - measureStart);
        long sum = 0;
        List<KeyValuePair<MethodBase, long>> list = new List<KeyValuePair<MethodBase, long>>(bytes);
        for (int i = 0; i < list.Count; i++) sum += list[i].Value;
        list.Sort(delegate (KeyValuePair<MethodBase, long> a, KeyValuePair<MethodBase, long> b) { return b.Value.CompareTo(a.Value); });
        int gcs = GC.CollectionCount(0) - gcStart;
        StringBuilder sb = new StringBuilder();
        sb.Append("[FirstPersonLoD] garbage census (" + secs.ToString("0.0") + " s in " + (VRFP.CurrentRoomName ?? "?") + ", " + targets.Count + " methods wrapped" + (failed > 0 ? ", " + failed + " could not be" : "")
            + ", " + gcs + " collections): the game's update methods made " + (sum / 1024f / secs).ToString("0") + " KB/s (the mod's own hooks on a few of them are counted with them)");
        for (int i = 0; i < list.Count && i < 15; i++)
        {
            MethodBase m = list[i].Key;
            int c; calls.TryGetValue(m, out c);
            sb.Append("\n  " + (list[i].Value / 1024f / secs).ToString("0.0").PadLeft(7) + " KB/s  " + m.DeclaringType.Name + "." + m.Name + "  (" + (c / secs).ToString("0") + " allocating calls/s, "
                + (c > 0 ? (list[i].Value / (float)c).ToString("0") : "0") + " bytes each)");
        }
        Debug.Log(sb.ToString());
    }
}

// ---------------------------------------------------------------------------------------------
// NGUI rebuilds (NGUIParentFix). The game's HUDs (all four players') hang under Main Camera,
// which follows the player. NGUI 2 checks Transform.hasChanged on every widget, and Unity sets that
// flag on every child when a parent moves, so each camera move made every HUD panel rebuild its
// whole geometry: ~9 MB of garbage a second, the game's garbage collection hitches (NGUI census,
// v0.9.0). Panel geometry is in the panel's own space, so a panel moving as a whole never needs
// a rebuild (NGUI still moves its draw calls every frame). Before each panel's LateUpdate, when
// the panel or something above it moved, the flag is cleared on every transform in it whose own
// local position, rotation and scale did not change; real changes still rebuild. Drawing is the
// same in both views; this only removes the wasted work.
// ---------------------------------------------------------------------------------------------
public static class NGUIFix
{
    private struct TRS { public Vector3 p; public Quaternion r; public Vector3 s; }
    private static readonly Dictionary<int, TRS> last = new Dictionary<int, TRS>();
    public static int Cleared, Kept;

    public static string Install()
    {
        if (!FPConfig.NGUIParentFix) return "off (NGUIParentFix=false)";
        MethodInfo lu = typeof(UIPanel).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        string e = HarmonyShim.Prefix(lu, typeof(NGUIFix).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static));
        return e == null ? "on (HUD panels rebuild only when something in them changes)" : "FAILED: " + e;
    }

    public static void Prefix(UIPanel __instance)
    {
        try
        {
            Transform t = __instance.cachedTransform;
            if (t == null || !t.hasChanged) return;
            Walk(t);
            t.hasChanged = false;
        }
        catch (Exception) { }
    }

    private static void Walk(Transform x)
    {
        for (int i = 0; i < x.childCount; i++)
        {
            Transform c = x.GetChild(i);
            if (c.hasChanged)
            {
                int id = c.GetInstanceID();
                TRS now; now.p = c.localPosition; now.r = c.localRotation; now.s = c.localScale;
                TRS was;
                if (last.TryGetValue(id, out was) && was.p == now.p && was.r == now.r && was.s == now.s) { c.hasChanged = false; Cleared++; }
                else { last[id] = now; Kept++; }
            }
            if (c.childCount > 0) Walk(c);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// NGUI census (UIProbe, once per session). The garbage census found the game's garbage is almost
// all NGUI: UIPanel.LateUpdate rebuilding a panel's geometry (~33 KB of new arrays each time,
// ~290 times a second in first person; the tabletop view makes about a third as much). A panel
// rebuilds when one of its widgets changes (moved, re-coloured, text changed, switched on or off).
// After a minute of first person in the dungeon this hooks, for 10 seconds, the widget change
// check (UIWidget.UpdateGeometry), the panel rebuild (UIPanel.Fill) and widgets switching on and
// off, and logs which widgets change every frame and why, and which panels rebuild how often.
// Five small hooks, removed afterwards.
// ---------------------------------------------------------------------------------------------
public static class UIProbe
{
    private static readonly Dictionary<string, int> changed = new Dictionary<string, int>(), moved = new Dictionary<string, int>(), toggled = new Dictionary<string, int>(), fills = new Dictionary<string, int>();
    private static readonly Dictionary<string, long> fillBytes = new Dictionary<string, long>();
    private static readonly List<KeyValuePair<MethodBase, MethodInfo>> hooks = new List<KeyValuePair<MethodBase, MethodInfo>>();
    private static readonly Dictionary<int, string> names = new Dictionary<int, string>();
    private static int stage;
    private static float drivenFor, start;
    public static bool On;

    private static string Path(Component c)
    {
        string n;
        if (names.TryGetValue(c.GetInstanceID(), out n)) return n;
        StringBuilder sb = new StringBuilder(c.GetType().Name + " ");
        Transform t = c.transform;
        List<string> parts = new List<string>();
        for (int i = 0; t != null && i < 5; i++, t = t.parent) parts.Insert(0, t.name);
        sb.Append(string.Join("/", parts.ToArray()));
        n = sb.ToString();
        names[c.GetInstanceID()] = n;
        return n;
    }
    private static void Add(Dictionary<string, int> d, string k) { int c; d.TryGetValue(k, out c); d[k] = c + 1; }

    public static void GeomPost(UIWidget __instance, bool __result, bool __1)
    {
        if (!On || !__result || __instance == null) return;
        string k = Path(__instance);
        Add(changed, k);
        if (__1) Add(moved, k);
    }
    public static void FillPre(out long __state) { __state = On ? GC.GetTotalMemory(false) : 0; }
    public static void FillPost(UIPanel __instance, Material __0, long __state)
    {
        if (!On || __instance == null) return;
        string k = Path(__instance) + " [" + (__0 != null ? __0.name : "?") + "]";
        Add(fills, k);
        long d = GC.GetTotalMemory(false) - __state;
        if (__state != 0 && d > 0) { long b; fillBytes.TryGetValue(k, out b); fillBytes[k] = b + d; }
    }
    public static void TogglePost(UIWidget __instance) { if (On && __instance != null) Add(toggled, Path(__instance)); }

    private static void Hook(MethodBase target, MethodInfo pre, MethodInfo post, StringBuilder note, string what)
    {
        if (target == null) { note.Append(" " + what + " not found;"); return; }
        string e = pre != null && post != null ? HarmonyShim.PrePost(target, pre, post) : post != null ? HarmonyShim.Postfix(target, post) : HarmonyShim.Prefix(target, pre);
        if (e != null) { note.Append(" " + what + ": " + e + ";"); return; }
        if (pre != null) hooks.Add(new KeyValuePair<MethodBase, MethodInfo>(target, pre));
        if (post != null) hooks.Add(new KeyValuePair<MethodBase, MethodInfo>(target, post));
    }

    public static void Tick(bool driving, string room)
    {
        if (!FPConfig.UIProbe || stage == 3) return;
        try
        {
            if (stage == 0)
            {
                if (driving && room != null && room != "Tavern") drivenFor += Time.unscaledDeltaTime;
                if (drivenFor < 60f) return;
                BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                BindingFlags ps = BindingFlags.Public | BindingFlags.Static;
                StringBuilder note = new StringBuilder();
                MethodInfo geom = null;
                foreach (MethodInfo m in typeof(UIWidget).GetMethods(f)) if (m.Name == "UpdateGeometry" && m.GetParameters().Length == 3) geom = m;
                Hook(geom, null, typeof(UIProbe).GetMethod("GeomPost", ps), note, "UIWidget.UpdateGeometry");
                MethodInfo fill = typeof(UIPanel).GetMethod("Fill", f, null, new Type[] { typeof(Material) }, null)
                    ?? typeof(UIPanel).GetMethod("AAAAAAAAAAAAAAeo", f, null, new Type[] { typeof(Material) }, null);
                Hook(fill, typeof(UIProbe).GetMethod("FillPre", ps), typeof(UIProbe).GetMethod("FillPost", ps), note, "UIPanel.Fill");
                Hook(typeof(UIWidget).GetMethod("OnEnable", f, null, Type.EmptyTypes, null), null, typeof(UIProbe).GetMethod("TogglePost", ps), note, "UIWidget.OnEnable");
                Hook(typeof(UIWidget).GetMethod("OnDisable", f, null, Type.EmptyTypes, null), null, typeof(UIProbe).GetMethod("TogglePost", ps), note, "UIWidget.OnDisable");
                Debug.Log("[FirstPersonLoD] NGUI census: " + hooks.Count + " hooks on" + (note.Length > 0 ? " (" + note.ToString().Trim() + ")" : "") + "; measuring 10 s");
                start = Time.realtimeSinceStartup;
                On = true;
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                if (Time.realtimeSinceStartup - start < 10f) return;
                On = false;
                stage = 2;
                try { Report(Time.realtimeSinceStartup - start, room); } catch (Exception e) { Debug.Log("[FirstPersonLoD] NGUI census report failed: " + e.Message); }
                return;
            }
            if (stage == 2)
            {
                for (int i = 0; i < hooks.Count; i++) HarmonyShim.Unpatch(hooks[i].Key, hooks[i].Value);
                hooks.Clear();
                stage = 3;
                Debug.Log("[FirstPersonLoD] NGUI census: hooks removed");
            }
        }
        catch (Exception e)
        {
            On = false;
            Debug.Log("[FirstPersonLoD] NGUI census failed: " + e.GetType().Name + ": " + e.Message);
            stage = stage < 2 ? 2 : 3;
        }
    }

    private static void Top(StringBuilder sb, string title, Dictionary<string, int> d, float secs, Dictionary<string, int> sub, string subName, Dictionary<string, long> bytes)
    {
        List<KeyValuePair<string, int>> l = new List<KeyValuePair<string, int>>(d);
        l.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
        sb.Append("\n  " + title + (l.Count == 0 ? ": none" : ":"));
        for (int i = 0; i < l.Count && i < 14; i++)
        {
            int s; sub.TryGetValue(l[i].Key, out s);
            long by = 0; if (bytes != null) bytes.TryGetValue(l[i].Key, out by);
            sb.Append("\n    " + (l[i].Value / secs).ToString("0.0").PadLeft(6) + "/s  " + l[i].Key
                + (s > 0 ? "  (" + (100f * s / l[i].Value).ToString("0") + "% " + subName + ")" : "")
                + (by > 0 ? "  " + (by / 1024f / secs).ToString("0") + " KB/s" : ""));
        }
    }

    private static void Report(float secs, string room)
    {
        StringBuilder sb = new StringBuilder("[FirstPersonLoD] NGUI census (" + secs.ToString("0.0") + " s, " + (room ?? "?") + ", " + (Time.frameCount > 0 ? "" : "") + "first person):");
        Top(sb, "panels rebuilding their geometry", fills, secs, new Dictionary<string, int>(), "", fillBytes);
        Top(sb, "widgets that changed", changed, secs, moved, "because something above them moved", null);
        Top(sb, "widgets switched on or off", toggled, secs, new Dictionary<string, int>(), "", null);
        Debug.Log(sb.ToString());
    }
}

// ---------------------------------------------------------------------------------------------
// Forgiving player-1 input (beta). The game assigns one player per joystick in the order Windows
// lists them and gives the keyboard only to players left over, so virtual pads (Steam Input,
// OpenVR controllers exposed as joysticks) can leave player 1 with no keyboard and pinned to a
// device you are not holding. These cInput postfixes make player 1 also read the keyboard and
// every real gamepad (and its D-pad), using the game's own layouts, no matter how devices were
// assigned. OpenVR joysticks are excluded (VR already reaches the game through VRInputs).
// Other players keep the game's assignment. Config: ForgivingP1 (turn off for local co-op, where
// every pad driving player 1 would be wrong).
// ---------------------------------------------------------------------------------------------
public static class ForgivingInput
{
    public static string Note = "not installed";

    // action base name -> keyboard key and the beta's Windows pad button (Setup.ResetControl)
    private static readonly string[] Names   = { "Jump", "Use", "Next", "Previous", "Drop", "HotkeyA", "HotkeyB" };
    private static readonly KeyCode[] Keys    = { KeyCode.Z, KeyCode.X, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.Q, KeyCode.W };
    private static readonly int[] PadButtons  = { 1, 0, 3, 2, 7, 5, 4 };

    private static bool[] realPad = new bool[9];   // index 1..8 = Unity joystick number
    private static float nextScan;
    private static bool[] axisBroken = new bool[9 * 8];
    private static string padsNote = "";

    public static void Install()
    {
        if (!FPConfig.ForgivingP1) { Note = "off (ForgivingP1=false)"; return; }
        Type c = typeof(cInput);
        Type self = typeof(ForgivingInput);
        BindingFlags ps = BindingFlags.Public | BindingFlags.Static;
        string e1 = HarmonyShim.Postfix(c.GetMethod("GetAxis", ps, null, new Type[] { typeof(string) }, null), self.GetMethod("AxisPostfix", ps));
        string e2 = HarmonyShim.Postfix(c.GetMethod("GetAxisRaw", ps, null, new Type[] { typeof(string) }, null), self.GetMethod("AxisPostfix", ps));
        string e3 = HarmonyShim.Postfix(c.GetMethod("GetButton", ps, null, new Type[] { typeof(string) }, null), self.GetMethod("ButtonPostfix", ps));
        string e4 = HarmonyShim.Postfix(c.GetMethod("GetButtonDown", ps, null, new Type[] { typeof(string) }, null), self.GetMethod("ButtonDownPostfix", ps));
        string e5 = HarmonyShim.Postfix(c.GetMethod("GetButtonUp", ps, null, new Type[] { typeof(string) }, null), self.GetMethod("ButtonUpPostfix", ps));
        string err = e1 ?? e2 ?? e3 ?? e4 ?? e5;
        Note = err == null ? "on (keyboard + any real gamepad drive player 1)" : err;
        Debug.Log("[FirstPersonLoD] Forgiving player-1 input: " + Note);
    }

    private static void Scan()
    {
        if (Time.realtimeSinceStartup < nextScan) return;
        nextScan = Time.realtimeSinceStartup + 1f;
        string[] n;
        try { n = Input.GetJoystickNames(); } catch (Exception) { return; }
        StringBuilder sb = new StringBuilder();
        for (int j = 1; j <= 8; j++)
        {
            string name = j - 1 < n.Length ? n[j - 1] : "";
            realPad[j] = name.Length > 0 && name.IndexOf("OpenVR", StringComparison.OrdinalIgnoreCase) < 0;
            if (realPad[j]) sb.Append(" " + j + ":" + name);
        }
        string pn = sb.ToString();
        if (pn != padsNote)
        {
            padsNote = pn;
            Debug.Log("[FirstPersonLoD] Forgiving input reads gamepads:" + (pn.Length > 0 ? pn : " (none)"));
        }
    }

    private static float PadAxis(int joy, int axis)
    {
        int k = joy * 8 + (axis - 1);
        if (axis < 1 || axis > 8 || axisBroken[k]) return 0f;
        try { return Input.GetAxisRaw("Joy" + joy + " Axis " + axis); }
        catch (Exception) { axisBroken[k] = true; return 0f; }
    }

    private static float Strongest(float a, float b) { return Mathf.Abs(b) > Mathf.Abs(a) ? b : a; }

    // horz1: +right   vert1: +down (the game's "Up" is the negative side)
    public static void AxisPostfix(string axisName, ref float __result)
    {
        try
        {
            bool h = axisName == "horz1";
            bool v = axisName == "vert1";
            if (!h && !v) return;
            Scan();
            float val = 0f;
            if (h) val = (Input.GetKey(KeyCode.RightArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
            else   val = (Input.GetKey(KeyCode.DownArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.UpArrow) ? 1f : 0f);
            for (int j = 1; j <= 8; j++)
            {
                if (!realPad[j]) continue;
                float stick = PadAxis(j, h ? 1 : 2);             // left stick X / Y (down +)
                float dpad  = h ? PadAxis(j, 6) : -PadAxis(j, 7); // D-pad X / Y (up + on Windows)
                if (Mathf.Abs(stick) < 0.25f) stick = 0f;
                val = Strongest(val, Strongest(stick, dpad));
            }
            // keep the game's own reading when it is stronger (e.g. a binding the player customized)
            __result = Strongest(val, __result);
        }
        catch (Exception) { }
    }

    private static int Index(string description)
    {
        if (description == null || description.Length < 2 || description[description.Length - 1] != '1') return -1;
        string b = description.Substring(0, description.Length - 1);
        for (int i = 0; i < Names.Length; i++) if (Names[i] == b) return i;
        return -1;
    }

    private static bool PadButton(int button, int mode) // 0 held, 1 down, 2 up
    {
        Scan();
        for (int j = 1; j <= 8; j++)
        {
            if (!realPad[j]) continue;
            KeyCode k = (KeyCode)(350 + (j - 1) * 20 + button);
            if (mode == 0 ? Input.GetKey(k) : mode == 1 ? Input.GetKeyDown(k) : Input.GetKeyUp(k)) return true;
        }
        return false;
    }

    public static void ButtonPostfix(string description, ref bool __result)
    {
        if (__result) return;
        int i = Index(description); if (i < 0) return;
        try { __result = Input.GetKey(Keys[i]) || PadButton(PadButtons[i], 0); } catch (Exception) { }
    }

    public static void ButtonDownPostfix(string description, ref bool __result)
    {
        if (__result) return;
        int i = Index(description); if (i < 0) return;
        try { __result = Input.GetKeyDown(Keys[i]) || PadButton(PadButtons[i], 1); } catch (Exception) { }
    }

    public static void ButtonUpPostfix(string description, ref bool __result)
    {
        if (__result) return;
        int i = Index(description); if (i < 0) return;
        try { __result = Input.GetKeyUp(Keys[i]) || PadButton(PadButtons[i], 2); } catch (Exception) { }
    }
}
#endif

public static class FPConfig
{
    public static KeyCode ToggleKey = KeyCode.F6;
    public static float Fov = 65f;
    public static float EyeHeight = 0.35f;
    public static float ForwardOffset = 0f;
    public static float MouseSensitivity = 2f;
    public static bool InvertY = false;
    public static float NearClip = 0.02f;
    public static int BillboardMode = 2;   // 0 off, 1 free, 2 quantized 45 deg
    public static bool KeepFlip = true;
    public static bool FlipBillboard = false;
    public static bool HideOwnBody = true;
    public static float FogStart = -1f;    // > 0 overrides fog start distance while driving
    public static bool ThickOn = false;        // stacked-layer thickness (off: facing alone solved the flat look; costly)
    public static float ThickRatio = 0.18f;    // thickness as a fraction of the sprite quad's width
    public static int ThickLayers = 3;         // extra copies stacked through the thickness
    public static float ThickRange = 8f;       // only thicken sprites within this many world units of the viewer
    public static float FarClip = 30f;         // first-person camera view distance in world units (0 = game default)
    public static bool BillboardAll = false;   // true also turns static animated props (torches, decals)
    public static bool InputFix = true;    // InputGuard: keyboard always on P1, controllers claimed by button press
    // VR (beta branch)
    public static bool VRAuto = true;          // first person engages automatically in VR
    public static float VRScaleMul = 1f;       // multiplier on the auto world scale
    public static float MinEyeMeters = 1.1f;   // floor for measured eye height (seated tracking origins read ~0)
    public static float SnapTurn = 90f;
    public static bool FlipDepth = false;      // flip stick forward/back if forward walks you backward
    public static KeyCode RecenterKey = KeyCode.F5;
    public static bool VRHarmony = true;       // false skips the VRInputs patch (stick then moves along world axes)
    public static bool VRBindings = true;      // beta: mod writes its SteamVR action manifest + controller bindings
    public static string SnapTurnStick = "right"; // "right" (convention) or "left" (left-stick flicks turn instead of strafing)
    public static bool ForgivingP1 = true;     // beta: player 1 also reads keyboard + any real gamepad (off for local co-op)
    public static float EyeFrac = 0.88f;       // VR: eye height as a fraction of the collision body (0 = use EyeHeight)
    public static float NearMeters = 0.05f;    // VR: near clip in real meters (converted by world scale)
    public static float HeadLeash = 0.9f;      // VR: keep the head within this fraction of the body radius (0 = off)
    public static bool AutoFaceRoom = true;    // VR: face into the room on start and after every door/stairs
    public static bool DarkenDoors = false;    // VR: show the game's post-door fade in the headset (off = walk straight through)
    public static bool DarkenStairs = true;    // VR: show the game's stairs fade in the headset (its own timing)
    public static bool DarkenLoading = true;   // VR: show the game's other black-drop fades (loading, death) in the headset
    public static float MaxDarkSeconds = 6f;   // VR: never keep the headset dark longer than this for one game fade
    public static bool HideGameDrop = true;    // VR: hide the game's tabletop black drop from the world in first person
    public static float FadeSeconds = 0.3f;    // VR: fade-back length when a game fade is cut short
    public static bool FPFog = false;          // VR: keep the game's VR fog in first person (tuned for the tabletop view)
    public static int FPNearLights = 4;        // VR: lights whose reach covers you that always render per-pixel
    public static float FPAmbientMin = 0.04f;  // VR: ambient floor in first person (the game's ambient is black)
    public static float JumpDist = 1.5f;       // VR: a player move longer than this in one frame counts as a teleport
    public static int FPPixelLights = 5;       // VR: per-pixel light cap while in first person (-1 = game default)
    public static int FPShadowLights = 0;      // VR: shadows kept only on the nearest N casters (-1 = all)
    public static float FPShadowDistance = 15f; // VR: shadow distance while in first person (0 = game default)
    public static bool AimWithHead = true;     // VR: turn the player (and its attack box) to head yaw
    public static bool WeaponView = true;      // VR: show the held weapon face-on in front of you
    public static float WeaponScale = 1f;      // VR: weapon view size multiplier
    public static float WeaponDistance = 0.6f; // VR: weapon distance ahead, in weapon-quad widths
    public static float WeaponRight = 0.15f;   // VR: weapon offset to the right, in quad widths
    public static float WeaponUp = 0f;         // VR: weapon offset up, in quad widths
    public static bool WeaponMirror = true;    // VR: mirror the weapon art (Insert toggles)
    public static bool Weapon6DOF = true;      // VR: weapon held in your controller, swings hit where it is
    public static string WeaponHand = "right"; // VR: "right" or "left"
    public static float WeaponPitch = -45f;    // VR: blade angle vs the controller, degrees (negative = up; ; and ' adjust)
    public static bool WeaponCross = true;     // VR: second crossed plane so the blade reads from every angle
    public static bool WeaponGripFlip = false; // VR: hold the other end of the sprite (/ toggles)
    public static float SwingSpeed = 4f;     // VR: blade tip speed (m/s) that triggers an attack (0 = trigger only)
    public static bool DoorLog = true;
    public static bool ClearUsedDoors = true;  // VR: a door/stairs you are outside of is never left marked "just used" (see VRFP.ClearUsedMarks)         // debug: log each door box you step into with the game's door state
    public static int HudEveryFrames = 4;      // VR: wrist HUD refresh (1 = every frame)
    public static int FPShadowRes = 256;       // VR: cube shadow size for kept shadowed lights (0 = game's automatic, up to 1024 per face)
    public static float LightHysteresis = 1.5f; // VR: head start (game units) a chosen light keeps before another replaces it
    public static bool WarmupShaders = true;   // VR: compile every loaded shader variant once when first person first engages (one pause, no hitches later)
    public static bool RoomReshape = true;     // VR: first person rooms get deeper (RoomExtraDepth) with a brick front wall
    public static float RoomExtraDepth = 1f;   // VR: extra room depth toward the open side, tile units (1 = one brick row)
    public static bool RoomFrontWall = true;   // VR: brick wall along the (new) open side
    public static bool RoomLightReach = true;  // VR: torches in a deepened room reach its new front as far as they reached the old one
    public static bool TorchLog = true;
    public static float RocketMinSpeed = 4f;   // VR: aimed bazooka rockets fly at least this fast (units/s) until they lock on
    public static string GiveAllSkip = "neko,crystal,guitar"; // debug give-all leaves out items whose names contain these
    public static bool InvMenu = true;         // VR: inventory screen (Steam Frame: hold the left bumper)
    public static float InvHoldSeconds = 0.35f; // VR: how long to hold the bumper before the inventory opens
    public static float BrickRough = 0.75f;       // VR: rough masonry strength (0 = the game's plain bricks, 1 = default, 2 = rubble)
    public static int BrickDetail = 2;         // VR: rough brick smoothness (0-3; each step is 4x the triangles)
    public static int BrickMaxVerts = 300000;  // VR: vertex budget per room for rough bricks (32-bit game: memory)
    public static bool BrickGloss = true;      // VR: bricks get one of three shininess levels (damp / normal / dry)
    public static float FPRenderScale = 0f;    // VR: eye resolution multiple in first person (0 = leave alone: SteamVR's own resolution setting decides)
    public static int FPMsaa = -1;             // VR: anti-aliasing samples in first person (0 off, 2, 4, 8; -1 = leave alone, the game already uses 4)
    public static bool StoneHD = true;         // VR: HD stone textures on the rough bricks (F1 flips HD / game textures)
    public static int StoneHDScale = 32;       // VR: HD pixels per game pixel (8-48; 32 = 256 x 256 per brick face)
    public static float StoneHDStrength = 1.75f;  // VR: HD stone detail (0.5 subtle, 1 default, 1.5 heavy)
    public static bool StoneHDDump = false;    // debug: save the game's brick textures and the first HD results under BepInEx/FirstPersonLoD_dump/stone (slow: a stall per texture)
    public static int StoneHDMaxSize = 1280;   // VR: longest side of an HD stone texture (graphics memory)
    public static int StoneHDKeep = 12;        // VR: HD stone textures kept (about 3 floors); older ones go back to the game's
    public static float BrickBudgetMs = 3f;    // VR: time per frame spent preparing a newly entered room's first-person bricks
    public static int BrickBatchGroup = 120;   // VR: bricks per merged group (smaller = shorter work per frame, a few more draw calls)
    public static bool BrickStaticBatch = true; // VR: merge the first-person bricks (static batching)
    public static bool Statues3D = true;       // VR: statues (flat picture cards) get a rounded 3D shape and HD stone in first person
    public static string StatueNames = "statue"; // VR: objects whose name contains one of these (comma separated) are treated as statues
    public static float StatueDepth = 1.2f;  // VR: carved figures are at most this many picture pixels deep on each side (the body; wings and edges less)
    public static float StatueRound = 5f;      // (unused since v0.9.1)
    public static string StatueTextures = "angel"; // VR: figures whose texture name contains one of these (comma separated) are carved in 3D
    public static string StoneHDTextures = "Block,drain"; // VR: brick textures (name starts with one of these, comma separated) that get the HD stone treatment
    public static string PropWood = "crate";   // VR: props whose texture name is one of these (comma separated) are built as 3D crates in first person
    public static bool AudioGuard = true;      // restart the game's music if Windows switches or resets the sound device mid-game
    public static float MenuHoldSeconds = 0.5f; // VR: a thumbstick click pauses only when held this long with the stick centred (Touch, Index; the Frame pauses on its right Menu button)
    public static float TouchHandsHold = 0.8f; // VR: Quest/Touch: hold the right grip this long for bare hands (0 = off)
    public static bool PickupMagnet = true;    // VR: coins and items on the floor within PickupRadius slide to you and are picked up
    public static float PickupRadius = 1.2f;   // VR: magnet reach, game units across the floor (a brick is 0.5)
    public static float PickupPull = 7f;       // VR: magnet speed, game units per second
    public static bool TwoHandGuns = true;     // VR: the free hand on a gun's barrel holds it in both hands (it points from hand to hand)
    public static float TwoHandReach = 0.1f;   // VR: how close (m) to the barrel's line the free hand must come to take hold
    public static float TwoHandMin = 0.08f;    // VR: the free hand must be at least this far (m) in front of the gun hand
    public static float TwoHandMax = 0.6f;     // VR: and at most this far (m)
    public static bool OffHandUse = true;      // VR: the free hand's trigger hits a switch it touches or points at
    public static float OffHandReach = 0.2f;   // VR: how far (m) from the free hand a switch counts as touched
    public static float OffHandPoint = 0.8f;   // VR: how far (m) the free hand reaches by pointing (0 = touch only)
    public static string OffHandTargets = "Switch,ActivateOnHit"; // VR: game scripts the free hand can hit (comma separated)
    public static bool Crates3D = true;        // VR: crates get boards and an iron binding in first person
    public static bool RoomCeilings = true;    // VR: rooms get a brick ceiling under their invisible roof in first person
    public static bool MetalBlocks = true;     // VR: plain untextured metal blocks get a brushed, pitted iron surface in first person
    public static string MetalNames = "Iron";  // VR: material names (start of name, comma separated) treated as metal blocks
    public static bool DoorTunnels = true;     // VR: doorways get brick jambs and a lintel in first person, with a shallower black fill
    public static float DoorBlackDepth = 0.5f; // VR: how much of a doorway's depth the black fill keeps (1 = the game's full box)
    public static bool TavernWalls = true;     // VR: the Tavern gets real walls and a ceiling in first person (where the game has invisible ones)
    public static bool TavernShowcase = true;  // VR: richer lighting in the Tavern (more per-pixel lights, one shadow); drops back if the frame rate suffers
    public static int TavernPixelLights = 8;   // VR: per-pixel light cap in the Tavern with TavernShowcase (forward)
    public static bool TavernDeferred = true;  // VR: the Tavern showcase uses the game's deferred rendering (all lights per pixel, cheap shadows, no MSAA)
    public static int TavernTorches = 2;       // VR: torches per side wall of the Tavern in first person (0 = none)
    public static float TavernTorchRange = 4.5f;   // VR: reach of those torches (game units; a brick is 0.5)
    public static float TavernTorchIntensity = 1.6f; // VR: brightness of those torches
    public static bool NGUIParentFix = true;   // HUD panels skip the geometry rebuild when only the camera they hang under moved (garbage, hitches)
    public static bool GarbageProbe = false;   // debug: once per session, 10 s census of which game scripts make garbage (after a minute of first person; ~1 s of hitches)
    public static bool UIProbe = true;         // debug: once per session, 10 s census of which NGUI widgets and panels rebuild (after a minute of first person)
    public static bool SaveGuard = true;       // never write or load a save with no living player in it (loads to a black screen)
    public static bool FPAniso = true;         // VR: anisotropic texture filtering in first person
    public static bool ReturnAfterUse = true;  // VR: after Drink/Eat/Use/Wear from the inventory, go back to the item you were holding
    public static bool HandsToggleBack = true; // VR: holding the right bumper again goes back to the item you had before hands
    public static bool InvPause = true;        // VR: the game is paused while the inventory is open
    public static float InvPointerPitch = 30f; // VR: pointer beam angle down from the controller's axis (degrees)
    public static float InvDistance = 0.6f;    // VR: inventory panel distance in front of you (meters)
    public static float InvScale = 1f;         // VR: inventory panel size        // debug: log every torch the game switches on or off, and what switched it
    public static bool MergeBricks = true;     // VR: draw each room's plain wall/floor bricks from merged meshes (static batching)
    public static bool LogButtons = true;      // debug: log which physical button triggers each game action (first 3 presses each)
    public static float StallMs = 60f;         // debug: log any frame longer than this with where its time went
    public static int TextureCameraEvery = 10; // VR: game cameras that film into a texture (in-world screens) refresh every N frames
    public static bool SkipHiddenCameras = true; // VR: switch off cameras whose picture the head camera fully covers
    public static int GiveAllAmmo = 99;        // debug: ammo put into every gun/wand by give-all
    public static bool PerfProbe = true;       // debug: once per floor, compare render setups for 4 s each and log a table (lighting changes briefly)
    public static float DioramaBelowEye = 0.3f; // VR: tabletop view, the room's middle this many meters below your eyes (0 = game's own height)
    public static string HandItemShader = "cheap"; // VR: "cheap" (one-pass vertex lit held items) or "game" (the item's own multi-pass shader)
    public static bool ShowOffHand = true;     // VR: off-hand items of your character (the Knight's shield) on the left controller
    public static string[] OffHandNames = new string[] { "shield" }; // VR: body renderer names (lower case, comma list) shown on the left hand
    public static float OffHandScale = 1f;     // VR: off-hand item size multiplier
    public static Vector3 OffHandOffset = new Vector3(0f, 0f, 0.06f); // VR: off-hand item centre vs left controller, meters (x right, y up, z forward)
    public static Vector3 OffHandAngles = new Vector3(0f, 0f, 0f); // VR: off-hand item rotation vs left controller, degrees (default: face where your fist points, upright with thumb up)
    public static float AttackTimeout = 2f;    // VR: end an attack the game has not finished after this many seconds
    public static bool DumpSprites = true;     // debug: save your character's sprite sheets as PNGs under BepInEx/FirstPersonLoD_dump
    public static bool MenuRecenter = true;    // VR: on pause/menus, re-centre the game's view on where your head is (the game's own Reorient)
    public static float SwingCooldown = 0.35f; // VR: seconds between motion attacks
    public static string ThrustMode = "look";  // VR: attack lunge "look" (along blade/view), "off", or "game" (world X)
    public static string FPRenderPath = "game";    // VR: "game" (the game's deferred path: light cost does not grow with the bricks; no MSAA) or "forward" (pixel light cap applies, MSAA); F3 flips
    public static int FPDeferredShadows = 1;   // VR: on the deferred path, the nearest N shadow-casting lights keep their shadows
    public static float FPHeadLight = 1f;      // VR: carried light intensity (0 = off)
    public static float HeadLightMeters = 6f;  // VR: carried light reach in meters
    public static float FPAmbient = 1.5f;      // VR: ambient light multiplier in first person (1 = game)
    public static Vector3 GripOffset = new Vector3(0f, -0.02f, -0.04f); // VR: wrist pivot vs controller origin, meters (x right, y up, z forward)
    public static float ButtonSwingDegrees = 110f; // VR: arc of the chop played when the attack button is used (0 = off)
    public static string HudMode = "wrist";    // VR: "wrist" (screen on the back of the left hand) or "game" (the game's own placement)
    public static int HudLayer = 31;
    public static bool GiveAllCombo = true;    // hold B (next item) + left trigger 2 s to get every item and full ammo
    public static bool GiveAllItems = false;   // debug: give player 1 every item whenever first person engages
    public static float DoorStepOut = 0.8f;
    public static float FaceForwardBias = 1.5f; // VR: on arrival, how strongly to face forward (-Z) vs along the room toward its centre
    public static bool AimProjectiles = true;
    public static float DoorExitMargin = 0.35f;
    public static float BounceGuard = 0f;
    public static float GunPitch = 30f;
    public static float ToggleHoldSeconds = 2f; // VR: hold A + left trigger this long to switch first person / tabletop view         // VR: angle of guns/wands vs the controller (0 = level with it; ; and ' adjust while holding one)      // VR: seconds after arriving during which the arrival door/stairs cannot send you back // VR: how far past the arrival door's edge you are stepped (game units)  // VR: bullets fly where the weapon in your hand points    // VR: after a door/stairs, step this far (game units) out of the doorway into the room (0 = off)
    public static string TriggerMode = "press"; // VR: "press" strikes when the trigger goes down (Minecraft); "release" = game (hold to charge)           // VR: spare render layer used to film the HUD for the wrist screen
    public static Color HudBackground = new Color(0.03f, 0.03f, 0.05f, 0.7f); // VR: wrist screen backing
    public static float HudWristWidth = 0.12f; // VR: wrist HUD width, meters
    public static Vector3 HudWristOffset = new Vector3(0f, 0.03f, -0.13f); // VR: wrist HUD vs left controller origin, meters (x right, y up, z forward)
    public static bool VRDrunkDrift = false;
    public static bool WeaponVoxels = true;    // VR: held items as 3D pixel blocks (Minecraft style); false = crossed flat planes
    public static float WeaponThickness = 1f;  // VR: block depth in pixels
    public static bool ShowHands = true;       // VR: gloved blocks on the controllers
    public static Vector3 HandSize = new Vector3(0.07f, 0.06f, 0.11f); // VR: hand block size, meters   // VR: let beer/confusion wander you around (the game's side-view effect); off for comfort
    public static float HudDistance = 0.7f;    // VR: HUD distance ahead, meters
    public static float HudDrop = 0.35f;       // VR: HUD below eye level, meters
    public static float HudWidth = 0.45f;      // VR: HUD width, meters
    public static float HudFollowDegrees = 30f; // VR: head turn before the HUD glides after your view
    public static int CfgVersion = 0;
    public const int LatestCfg = 19;

    public static string CfgPath()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(Path.Combine(root, "BepInEx"), Path.Combine("config", "FirstPersonLoD.cfg"));
    }

    public static void Load()
    {
        try
        {
            string p = CfgPath();
            if (!File.Exists(p)) { CfgVersion = LatestCfg; Save(); return; }   // a new install starts on today's defaults
            string[] lines = File.ReadAllLines(p);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                Apply(k, v);
            }
            // v0.5.0: v0.4.5 wrote FPPixelLights=4 into every cfg; forward rendering needs a little more
            if (CfgVersion < 2) { if (FPPixelLights == 4) FPPixelLights = 6; CfgVersion = 2; }
            // v0.5.1: hilt detection changed, so an old flip would now hold the blade
            if (CfgVersion < 3) { WeaponGripFlip = false; CfgVersion = 3; }
            // v0.5.2: v0.5.1 wrote HudMode=follow into every cfg; the wrist is the new default
            if (CfgVersion < 4) { if (HudMode == "follow") HudMode = "wrist"; CfgVersion = 4; }
            // v0.5.3: 2.5 m/s fired on ordinary arm movement
            if (CfgVersion < 5) { if (SwingSpeed == 2.5f) SwingSpeed = 4f; CfgVersion = 5; }
            // v0.6.7: snap turns are 90 degrees
            if (CfgVersion < 6) { SnapTurn = 90f; CfgVersion = 6; }
            // v0.6.8: no special handling of used passageways by the mod (the bounce guard blocked
            // the door you arrived through for 3 s)
            if (CfgVersion < 7) { BounceGuard = 0f; CfgVersion = 7; }
            // v0.6.9: shield turned 90 degrees clockwise (seen from above): its face now points
            // where your fist points instead of out of the back of your hand
            if (CfgVersion < 8)
            {
                if (OffHandAngles == new Vector3(0f, -90f, 0f)) OffHandAngles = Vector3.zero;
                if (OffHandOffset == new Vector3(-0.05f, 0f, -0.03f)) OffHandOffset = new Vector3(0f, 0f, 0.06f);
                CfgVersion = 8;
            }
            // v0.7.2 (render research): at most 5 per-pixel lights per object (head light + 4 nearest
            // forced + cap 5), no point-light shadows (the game's legacy shaders cannot show point
            // shadows in forward, yet Unity still rendered 6 cube faces per shadowed light), HUD
            // filmed every frame (even frame cost instead of a spike every 3rd frame)
            if (CfgVersion < 9)
            {
                if (FPNearLights == 6) FPNearLights = 4;
                if (FPPixelLights == 6) FPPixelLights = 5;
                if (FPShadowLights == 2) FPShadowLights = 0;
                if (HudEveryFrames == 3) HudEveryFrames = 1;
                CfgVersion = 9;
            }
            // v0.7.3: measured, the wrist HUD camera costs ~0.6 ms per render: every 4th frame
            if (CfgVersion < 10) { if (HudEveryFrames == 1) HudEveryFrames = 4; CfgVersion = 10; }
            // v0.7.5: guns aimed about 30 degrees high (the controller's forward points up from
            // where your hand aims): barrel pitched down by default
            if (CfgVersion < 11) { if (GunPitch == 0f) GunPitch = 30f; CfgVersion = 11; }
            // v0.8.7: the v0.8.6 picture defaults (eyes at 1.25x SteamVR's size) are withdrawn;
            // resolution stays with SteamVR's own setting, anti-aliasing with the game's 4x
            if (CfgVersion < 12) { if (FPRenderScale == 1.25f) FPRenderScale = 0f; if (FPMsaa == 4) FPMsaa = -1; CfgVersion = 12; }
            // v0.8.9: saving the HD textures as PNGs cost a stall of ~450 ms each; off by default
            if (CfgVersion < 13) { StoneHDDump = false; CfgVersion = 13; }
            // v0.9.0: the garbage census has answered (NGUI); its hooks cost ~1 s of hitches
            if (CfgVersion < 14) { GarbageProbe = false; CfgVersion = 14; }
            // v0.9.1: a slight roughness reads best (tried 0 to 2.5 in the headset); carved statues
            // replace the puffy ones (depth now per side of a flat carving)
            if (CfgVersion < 15) { if (BrickRough == 1f) BrickRough = 0.5f; if (StatueDepth == 4.5f) StatueDepth = 2f; CfgVersion = 15; }
            // v0.9.2: one step rougher, and HD stone detail one step under the strongest (tried in the headset)
            if (CfgVersion < 16) { if (BrickRough == 0.5f) BrickRough = 0.75f; if (StoneHDStrength == 1f) StoneHDStrength = 1.75f; CfgVersion = 16; }
            // v0.9.4: carved statues are chamfered and thinner (the old depth was the whole block)
            if (CfgVersion < 17) { if (StatueDepth == 2f) StatueDepth = 1.2f; CfgVersion = 17; }
            // v0.9.5: with walls, ceilings and doorways the forward path paid per brick per light
            // (the perf probe: forward 44-70 fps where deferred ran 88-90 in the same spots)
            if (CfgVersion < 18) { if (FPRenderPath == "forward") FPRenderPath = "game"; CfgVersion = 18; }
            if (CfgVersion < 19) { CfgVersion = 19; }
            Save(); // adds keys introduced by newer versions, keeps existing values
        }
        catch (Exception e) { Debug.LogError("[FirstPersonLoD] config load failed: " + e.Message); }
    }

    private static void Apply(string k, string v)
    {
        try
        {
            switch (k)
            {
                case "ToggleKey": ToggleKey = (KeyCode)Enum.Parse(typeof(KeyCode), v, true); break;
                case "FOV": Fov = F(v); break;
                case "EyeHeight": EyeHeight = F(v); break;
                case "ForwardOffset": ForwardOffset = F(v); break;
                case "MouseSensitivity": MouseSensitivity = F(v); break;
                case "InvertY": InvertY = B(v); break;
                case "NearClip": NearClip = F(v); break;
                case "BillboardMode": BillboardMode = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "KeepFlip": KeepFlip = B(v); break;
                case "FlipBillboard": FlipBillboard = B(v); break;
                case "HideOwnBody": HideOwnBody = B(v); break;
                case "FogStart": FogStart = F(v); break;
                case "ThickOn": ThickOn = B(v); break;
                case "ThickRatio": ThickRatio = F(v); break;
                case "ThickLayers": ThickLayers = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "ThickRange": ThickRange = F(v); break;
                case "FarClip": FarClip = F(v); break;
                case "BillboardAll": BillboardAll = B(v); break;
                case "InputFix": InputFix = B(v); break;
                case "VRAuto": VRAuto = B(v); break;
                case "VRScaleMul": VRScaleMul = F(v); break;
                case "MinEyeMeters": MinEyeMeters = F(v); break;
                case "SnapTurn": SnapTurn = F(v); break;
                case "FlipDepth": FlipDepth = B(v); break;
                case "RecenterKey": RecenterKey = (KeyCode)Enum.Parse(typeof(KeyCode), v, true); break;
                case "VRHarmony": VRHarmony = B(v); break;
                case "ForgivingP1": ForgivingP1 = B(v); break;
                case "VRBindings": VRBindings = B(v); break;
                case "SnapTurnStick": SnapTurnStick = v.ToLowerInvariant(); break;
                case "EyeFrac": EyeFrac = F(v); break;
                case "NearMeters": NearMeters = F(v); break;
                case "HeadLeash": HeadLeash = F(v); break;
                case "AutoFaceRoom": AutoFaceRoom = B(v); break;
                case "TeleportFade": break; // v0.6.4: replaced by DarkenDoors / DarkenStairs / DarkenLoading
                case "DarkenDoors": DarkenDoors = B(v); break;
                case "DarkenStairs": DarkenStairs = B(v); break;
                case "DarkenLoading": DarkenLoading = B(v); break;
                case "MaxDarkSeconds": MaxDarkSeconds = F(v); break;
                case "HideGameDrop": HideGameDrop = B(v); break;
                case "FPFog": FPFog = B(v); break;
                case "FPNearLights": FPNearLights = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "FPAmbientMin": FPAmbientMin = F(v); break;
                case "AttackTimeout": AttackTimeout = F(v); break;
                case "DumpSprites": DumpSprites = B(v); break;
                case "MenuRecenter": MenuRecenter = B(v); break;
                case "FadeSeconds": FadeSeconds = F(v); break;
                case "JumpDist": JumpDist = F(v); break;
                case "FPPixelLights": FPPixelLights = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "FPShadowLights": FPShadowLights = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "FPShadowDistance": FPShadowDistance = F(v); break;
                case "AimWithHead": AimWithHead = B(v); break;
                case "WeaponView": WeaponView = B(v); break;
                case "WeaponScale": WeaponScale = F(v); break;
                case "WeaponDistance": WeaponDistance = F(v); break;
                case "WeaponRight": WeaponRight = F(v); break;
                case "WeaponUp": WeaponUp = F(v); break;
                case "WeaponMirror": WeaponMirror = B(v); break;
                case "Weapon6DOF": Weapon6DOF = B(v); break;
                case "WeaponHand": WeaponHand = v.ToLowerInvariant(); break;
                case "WeaponPitch": WeaponPitch = F(v); break;
                case "WeaponCross": WeaponCross = B(v); break;
                case "WeaponGripFlip": WeaponGripFlip = B(v); break;
                case "SwingSpeed": SwingSpeed = F(v); break;
                case "SwingCooldown": SwingCooldown = F(v); break;
                case "ThrustMode": ThrustMode = v.ToLowerInvariant(); break;
                case "FPRenderPath": FPRenderPath = v.ToLowerInvariant(); break;
                case "FPDeferredShadows": FPDeferredShadows = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "FPHeadLight": FPHeadLight = F(v); break;
                case "HeadLightMeters": HeadLightMeters = F(v); break;
                case "FPAmbient": FPAmbient = F(v); break;
                case "CfgVersion": CfgVersion = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "GripOffset": GripOffset = V3(v); break;
                case "ButtonSwingDegrees": ButtonSwingDegrees = F(v); break;
                case "HudMode": HudMode = v.ToLowerInvariant(); break;
                case "HudDistance": HudDistance = F(v); break;
                case "HudDrop": HudDrop = F(v); break;
                case "HudWidth": HudWidth = F(v); break;
                case "HudFollowDegrees": HudFollowDegrees = F(v); break;
                case "HudWristWidth": HudWristWidth = F(v); break;
                case "HudLayer": HudLayer = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "GiveAllItems": GiveAllItems = B(v); break;
                case "GiveAllCombo": GiveAllCombo = B(v); break;
                case "DoorStepOut": DoorStepOut = F(v); break;
                case "FaceForwardBias": FaceForwardBias = F(v); break;
                case "AimProjectiles": AimProjectiles = B(v); break;
                case "DoorExitMargin": DoorExitMargin = F(v); break;
                case "BounceGuard": BounceGuard = F(v); break;
                case "GunPitch": GunPitch = F(v); break;
                case "ToggleHoldSeconds": ToggleHoldSeconds = F(v); break;
                case "TriggerMode": TriggerMode = v.ToLowerInvariant(); break;
                case "HudWristOffset": HudWristOffset = V3(v); break;
                case "ShowOffHand": ShowOffHand = B(v); break;
                case "HandItemShader": HandItemShader = v.ToLowerInvariant(); break;
                case "DioramaBelowEye": DioramaBelowEye = F(v); break;
                case "PerfProbe": PerfProbe = B(v); break;
                case "GiveAllAmmo": GiveAllAmmo = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "SkipHiddenCameras": SkipHiddenCameras = B(v); break;
                case "TextureCameraEvery": TextureCameraEvery = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "StallMs": StallMs = F(v); break;
                case "LogButtons": LogButtons = B(v); break;
                case "MergeBricks": MergeBricks = B(v); break;
                case "RoomReshape": RoomReshape = B(v); break;
                case "RoomExtraDepth": RoomExtraDepth = F(v); break;
                case "RoomFrontWall": RoomFrontWall = B(v); break;
                case "RoomLightReach": RoomLightReach = B(v); break;
                case "TorchLog": TorchLog = B(v); break;
                case "InvMenu": InvMenu = B(v); break;
                case "GiveAllSkip": GiveAllSkip = v; break;
                case "RocketMinSpeed": RocketMinSpeed = F(v); break;
                case "InvHoldSeconds": InvHoldSeconds = F(v); break;
                case "InvPause": InvPause = B(v); break;
                case "HandsToggleBack": HandsToggleBack = B(v); break;
                case "ReturnAfterUse": ReturnAfterUse = B(v); break;
                case "FPRenderScale": FPRenderScale = F(v); break;
                case "BrickRough": BrickRough = F(v); break;
                case "BrickDetail": BrickDetail = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "BrickMaxVerts": BrickMaxVerts = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "BrickGloss": BrickGloss = B(v); break;
                case "FPMsaa": FPMsaa = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "FPAniso": FPAniso = B(v); break;
                case "SaveGuard": SaveGuard = B(v); break;
                case "GarbageProbe": GarbageProbe = B(v); break;
                case "UIProbe": UIProbe = B(v); break;
                case "NGUIParentFix": NGUIParentFix = B(v); break;
                case "Statues3D": Statues3D = B(v); break;
                case "StatueNames": StatueNames = v; break;
                case "StatueDepth": StatueDepth = F(v); break;
                case "StatueRound": StatueRound = F(v); break;
                case "StatueTextures": StatueTextures = v; break;
                case "StoneHDTextures": StoneHDTextures = v; break;
                case "PropWood": PropWood = v; break;
                case "Crates3D": Crates3D = B(v); break;
                case "TwoHandGuns": TwoHandGuns = B(v); break;
                case "MenuHoldSeconds": MenuHoldSeconds = F(v); break;
                case "AudioGuard": AudioGuard = B(v); break;
                case "TouchHandsHold": TouchHandsHold = F(v); break;
                case "PickupMagnet": PickupMagnet = B(v); break;
                case "PickupRadius": PickupRadius = F(v); break;
                case "PickupPull": PickupPull = F(v); break;
                case "TwoHandReach": TwoHandReach = F(v); break;
                case "TwoHandMin": TwoHandMin = F(v); break;
                case "TwoHandMax": TwoHandMax = F(v); break;
                case "OffHandUse": OffHandUse = B(v); break;
                case "OffHandReach": OffHandReach = F(v); break;
                case "OffHandPoint": OffHandPoint = F(v); break;
                case "OffHandTargets": OffHandTargets = v; break;
                case "RoomCeilings": RoomCeilings = B(v); break;
                case "MetalBlocks": MetalBlocks = B(v); break;
                case "MetalNames": MetalNames = v; break;
                case "DoorTunnels": DoorTunnels = B(v); break;
                case "DoorBlackDepth": DoorBlackDepth = F(v); break;
                case "TavernWalls": TavernWalls = B(v); break;
                case "TavernShowcase": TavernShowcase = B(v); break;
                case "TavernPixelLights": TavernPixelLights = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "TavernTorches": TavernTorches = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "TavernDeferred": TavernDeferred = B(v); break;
                case "TavernTorchRange": TavernTorchRange = F(v); break;
                case "TavernTorchIntensity": TavernTorchIntensity = F(v); break;
                case "StoneHD": StoneHD = B(v); break;
                case "StoneHDScale": StoneHDScale = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "StoneHDStrength": StoneHDStrength = F(v); break;
                case "StoneHDDump": StoneHDDump = B(v); break;
                case "StoneHDMaxSize": StoneHDMaxSize = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "StoneHDKeep": StoneHDKeep = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "BrickBudgetMs": BrickBudgetMs = F(v); break;
                case "BrickBatchGroup": BrickBatchGroup = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "BrickStaticBatch": BrickStaticBatch = B(v); break;
                case "InvPointerPitch": InvPointerPitch = F(v); break;
                case "InvDistance": InvDistance = F(v); break;
                case "InvScale": InvScale = F(v); break;
                case "FPShadowRes": FPShadowRes = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "LightHysteresis": LightHysteresis = F(v); break;
                case "WarmupShaders": WarmupShaders = B(v); break;
                case "HudEveryFrames": HudEveryFrames = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "DoorLog": DoorLog = B(v); break;
                case "ClearUsedDoors": ClearUsedDoors = B(v); break;
                case "OffHandNames": { string[] a = v.ToLowerInvariant().Split(','); for (int i = 0; i < a.Length; i++) a[i] = a[i].Trim(); OffHandNames = a; } break;
                case "OffHandScale": OffHandScale = F(v); break;
                case "OffHandOffset": OffHandOffset = V3(v); break;
                case "OffHandAngles": OffHandAngles = V3(v); break;
                case "VRDrunkDrift": VRDrunkDrift = B(v); break;
                case "WeaponVoxels": WeaponVoxels = B(v); break;
                case "WeaponThickness": WeaponThickness = F(v); break;
                case "ShowHands": ShowHands = B(v); break;
                case "HandSize": HandSize = V3(v); break;
            }
        }
        catch { Debug.LogError("[FirstPersonLoD] bad config value: " + k + "=" + v); }
    }

    private static float F(string v) { return float.Parse(v, CultureInfo.InvariantCulture); }
    private static bool B(string v) { return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1"; }
    private static string S(float f) { return f.ToString(CultureInfo.InvariantCulture); }
    private static Vector3 V3(string v)
    {
        string[] p = v.Split(',');
        return new Vector3(F(p[0].Trim()), F(p[1].Trim()), F(p[2].Trim()));
    }

    public static void Save()
    {
        try
        {
            string p = CfgPath();
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# FirstPersonLoD settings. Deleting this file restores defaults.");
            sb.AppendLine("ToggleKey=" + ToggleKey);
            sb.AppendLine("FOV=" + Fov.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("EyeHeight=" + EyeHeight.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ForwardOffset=" + ForwardOffset.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("MouseSensitivity=" + MouseSensitivity.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("InvertY=" + (InvertY ? "true" : "false"));
            sb.AppendLine("NearClip=" + NearClip.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("BillboardMode=" + BillboardMode);
            sb.AppendLine("KeepFlip=" + (KeepFlip ? "true" : "false"));
            sb.AppendLine("FlipBillboard=" + (FlipBillboard ? "true" : "false"));
            sb.AppendLine("HideOwnBody=" + (HideOwnBody ? "true" : "false"));
            sb.AppendLine("FogStart=" + FogStart.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ThickOn=" + (ThickOn ? "true" : "false"));
            sb.AppendLine("ThickRatio=" + ThickRatio.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ThickLayers=" + ThickLayers);
            sb.AppendLine("ThickRange=" + ThickRange.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("FarClip=" + FarClip.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("BillboardAll=" + (BillboardAll ? "true" : "false"));
            sb.AppendLine("InputFix=" + (InputFix ? "true" : "false"));
            sb.AppendLine("VRAuto=" + (VRAuto ? "true" : "false"));
            sb.AppendLine("VRScaleMul=" + VRScaleMul.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("MinEyeMeters=" + MinEyeMeters.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("SnapTurn=" + SnapTurn.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("FlipDepth=" + (FlipDepth ? "true" : "false"));
            sb.AppendLine("RecenterKey=" + RecenterKey);
            sb.AppendLine("VRHarmony=" + (VRHarmony ? "true" : "false"));
            sb.AppendLine("ForgivingP1=" + (ForgivingP1 ? "true" : "false"));
            sb.AppendLine("VRBindings=" + (VRBindings ? "true" : "false"));
            sb.AppendLine("SnapTurnStick=" + SnapTurnStick);
            sb.AppendLine("EyeFrac=" + S(EyeFrac));
            sb.AppendLine("NearMeters=" + S(NearMeters));
            sb.AppendLine("HeadLeash=" + S(HeadLeash));
            sb.AppendLine("AutoFaceRoom=" + (AutoFaceRoom ? "true" : "false"));
            sb.AppendLine("DarkenDoors=" + (DarkenDoors ? "true" : "false"));
            sb.AppendLine("DarkenStairs=" + (DarkenStairs ? "true" : "false"));
            sb.AppendLine("DarkenLoading=" + (DarkenLoading ? "true" : "false"));
            sb.AppendLine("MaxDarkSeconds=" + S(MaxDarkSeconds));
            sb.AppendLine("HideGameDrop=" + (HideGameDrop ? "true" : "false"));
            sb.AppendLine("FPFog=" + (FPFog ? "true" : "false"));
            sb.AppendLine("FPNearLights=" + FPNearLights);
            sb.AppendLine("FPAmbientMin=" + S(FPAmbientMin));
            sb.AppendLine("AttackTimeout=" + S(AttackTimeout));
            sb.AppendLine("DumpSprites=" + (DumpSprites ? "true" : "false"));
            sb.AppendLine("MenuRecenter=" + (MenuRecenter ? "true" : "false"));
            sb.AppendLine("FadeSeconds=" + S(FadeSeconds));
            sb.AppendLine("JumpDist=" + S(JumpDist));
            sb.AppendLine("FPPixelLights=" + FPPixelLights);
            sb.AppendLine("FPShadowLights=" + FPShadowLights);
            sb.AppendLine("FPShadowDistance=" + S(FPShadowDistance));
            sb.AppendLine("AimWithHead=" + (AimWithHead ? "true" : "false"));
            sb.AppendLine("WeaponView=" + (WeaponView ? "true" : "false"));
            sb.AppendLine("WeaponScale=" + S(WeaponScale));
            sb.AppendLine("WeaponDistance=" + S(WeaponDistance));
            sb.AppendLine("WeaponRight=" + S(WeaponRight));
            sb.AppendLine("WeaponUp=" + S(WeaponUp));
            sb.AppendLine("WeaponMirror=" + (WeaponMirror ? "true" : "false"));
            sb.AppendLine("Weapon6DOF=" + (Weapon6DOF ? "true" : "false"));
            sb.AppendLine("WeaponHand=" + WeaponHand);
            sb.AppendLine("WeaponPitch=" + S(WeaponPitch));
            sb.AppendLine("WeaponCross=" + (WeaponCross ? "true" : "false"));
            sb.AppendLine("WeaponGripFlip=" + (WeaponGripFlip ? "true" : "false"));
            sb.AppendLine("SwingSpeed=" + S(SwingSpeed));
            sb.AppendLine("SwingCooldown=" + S(SwingCooldown));
            sb.AppendLine("ThrustMode=" + ThrustMode);
            sb.AppendLine("FPRenderPath=" + FPRenderPath);
            sb.AppendLine("FPDeferredShadows=" + FPDeferredShadows);
            sb.AppendLine("FPHeadLight=" + S(FPHeadLight));
            sb.AppendLine("HeadLightMeters=" + S(HeadLightMeters));
            sb.AppendLine("FPAmbient=" + S(FPAmbient));
            sb.AppendLine("GripOffset=" + S(GripOffset.x) + "," + S(GripOffset.y) + "," + S(GripOffset.z));
            sb.AppendLine("ButtonSwingDegrees=" + S(ButtonSwingDegrees));
            sb.AppendLine("HudMode=" + HudMode);
            sb.AppendLine("HudDistance=" + S(HudDistance));
            sb.AppendLine("HudDrop=" + S(HudDrop));
            sb.AppendLine("HudWidth=" + S(HudWidth));
            sb.AppendLine("HudFollowDegrees=" + S(HudFollowDegrees));
            sb.AppendLine("HudWristWidth=" + S(HudWristWidth));
            sb.AppendLine("HudLayer=" + HudLayer);
            sb.AppendLine("GiveAllItems=" + (GiveAllItems ? "true" : "false"));
            sb.AppendLine("GiveAllCombo=" + (GiveAllCombo ? "true" : "false"));
            sb.AppendLine("DoorStepOut=" + S(DoorStepOut));
            sb.AppendLine("FaceForwardBias=" + S(FaceForwardBias));
            sb.AppendLine("AimProjectiles=" + (AimProjectiles ? "true" : "false"));
            sb.AppendLine("DoorExitMargin=" + S(DoorExitMargin));
            sb.AppendLine("BounceGuard=" + S(BounceGuard));
            sb.AppendLine("GunPitch=" + S(GunPitch));
            sb.AppendLine("ToggleHoldSeconds=" + S(ToggleHoldSeconds));
            sb.AppendLine("TriggerMode=" + TriggerMode);
            sb.AppendLine("HudWristOffset=" + S(HudWristOffset.x) + "," + S(HudWristOffset.y) + "," + S(HudWristOffset.z));
            sb.AppendLine("ShowOffHand=" + (ShowOffHand ? "true" : "false"));
            sb.AppendLine("HandItemShader=" + HandItemShader);
            sb.AppendLine("DioramaBelowEye=" + S(DioramaBelowEye));
            sb.AppendLine("PerfProbe=" + (PerfProbe ? "true" : "false"));
            sb.AppendLine("GiveAllAmmo=" + GiveAllAmmo);
            sb.AppendLine("SkipHiddenCameras=" + (SkipHiddenCameras ? "true" : "false"));
            sb.AppendLine("TextureCameraEvery=" + TextureCameraEvery);
            sb.AppendLine("StallMs=" + S(StallMs));
            sb.AppendLine("LogButtons=" + (LogButtons ? "true" : "false"));
            sb.AppendLine("MergeBricks=" + (MergeBricks ? "true" : "false"));
            sb.AppendLine("RoomReshape=" + (RoomReshape ? "true" : "false"));
            sb.AppendLine("RoomExtraDepth=" + S(RoomExtraDepth));
            sb.AppendLine("RoomFrontWall=" + (RoomFrontWall ? "true" : "false"));
            sb.AppendLine("RoomLightReach=" + (RoomLightReach ? "true" : "false"));
            sb.AppendLine("TorchLog=" + (TorchLog ? "true" : "false"));
            sb.AppendLine("InvMenu=" + (InvMenu ? "true" : "false"));
            sb.AppendLine("GiveAllSkip=" + GiveAllSkip);
            sb.AppendLine("RocketMinSpeed=" + S(RocketMinSpeed));
            sb.AppendLine("InvHoldSeconds=" + S(InvHoldSeconds));
            sb.AppendLine("InvPause=" + (InvPause ? "true" : "false"));
            sb.AppendLine("HandsToggleBack=" + (HandsToggleBack ? "true" : "false"));
            sb.AppendLine("ReturnAfterUse=" + (ReturnAfterUse ? "true" : "false"));
            sb.AppendLine("FPRenderScale=" + S(FPRenderScale));
            sb.AppendLine("BrickRough=" + S(BrickRough));
            sb.AppendLine("BrickDetail=" + BrickDetail);
            sb.AppendLine("BrickMaxVerts=" + BrickMaxVerts);
            sb.AppendLine("BrickGloss=" + (BrickGloss ? "true" : "false"));
            sb.AppendLine("FPMsaa=" + FPMsaa);
            sb.AppendLine("FPAniso=" + (FPAniso ? "true" : "false"));
            sb.AppendLine("SaveGuard=" + (SaveGuard ? "true" : "false"));
            sb.AppendLine("GarbageProbe=" + (GarbageProbe ? "true" : "false"));
            sb.AppendLine("UIProbe=" + (UIProbe ? "true" : "false"));
            sb.AppendLine("NGUIParentFix=" + (NGUIParentFix ? "true" : "false"));
            sb.AppendLine("Statues3D=" + (Statues3D ? "true" : "false"));
            sb.AppendLine("StatueNames=" + StatueNames);
            sb.AppendLine("StatueDepth=" + S(StatueDepth));
            sb.AppendLine("StatueTextures=" + StatueTextures);
            sb.AppendLine("StoneHDTextures=" + StoneHDTextures);
            sb.AppendLine("PropWood=" + PropWood);
            sb.AppendLine("Crates3D=" + (Crates3D ? "true" : "false"));
            sb.AppendLine("TwoHandGuns=" + (TwoHandGuns ? "true" : "false"));
            sb.AppendLine("MenuHoldSeconds=" + S(MenuHoldSeconds));
            sb.AppendLine("AudioGuard=" + (AudioGuard ? "true" : "false"));
            sb.AppendLine("TouchHandsHold=" + S(TouchHandsHold));
            sb.AppendLine("PickupMagnet=" + (PickupMagnet ? "true" : "false"));
            sb.AppendLine("PickupRadius=" + S(PickupRadius));
            sb.AppendLine("PickupPull=" + S(PickupPull));
            sb.AppendLine("TwoHandReach=" + S(TwoHandReach));
            sb.AppendLine("TwoHandMin=" + S(TwoHandMin));
            sb.AppendLine("TwoHandMax=" + S(TwoHandMax));
            sb.AppendLine("OffHandUse=" + (OffHandUse ? "true" : "false"));
            sb.AppendLine("OffHandReach=" + S(OffHandReach));
            sb.AppendLine("OffHandPoint=" + S(OffHandPoint));
            sb.AppendLine("OffHandTargets=" + OffHandTargets);
            sb.AppendLine("RoomCeilings=" + (RoomCeilings ? "true" : "false"));
            sb.AppendLine("MetalBlocks=" + (MetalBlocks ? "true" : "false"));
            sb.AppendLine("MetalNames=" + MetalNames);
            sb.AppendLine("DoorTunnels=" + (DoorTunnels ? "true" : "false"));
            sb.AppendLine("DoorBlackDepth=" + S(DoorBlackDepth));
            sb.AppendLine("TavernWalls=" + (TavernWalls ? "true" : "false"));
            sb.AppendLine("TavernShowcase=" + (TavernShowcase ? "true" : "false"));
            sb.AppendLine("TavernPixelLights=" + TavernPixelLights);
            sb.AppendLine("TavernTorches=" + TavernTorches);
            sb.AppendLine("TavernDeferred=" + (TavernDeferred ? "true" : "false"));
            sb.AppendLine("TavernTorchRange=" + S(TavernTorchRange));
            sb.AppendLine("TavernTorchIntensity=" + S(TavernTorchIntensity));
            sb.AppendLine("StoneHD=" + (StoneHD ? "true" : "false"));
            sb.AppendLine("StoneHDScale=" + StoneHDScale);
            sb.AppendLine("StoneHDStrength=" + S(StoneHDStrength));
            sb.AppendLine("StoneHDDump=" + (StoneHDDump ? "true" : "false"));
            sb.AppendLine("StoneHDMaxSize=" + StoneHDMaxSize);
            sb.AppendLine("StoneHDKeep=" + StoneHDKeep);
            sb.AppendLine("BrickBudgetMs=" + S(BrickBudgetMs));
            sb.AppendLine("BrickBatchGroup=" + BrickBatchGroup);
            sb.AppendLine("BrickStaticBatch=" + (BrickStaticBatch ? "true" : "false"));
            sb.AppendLine("InvPointerPitch=" + S(InvPointerPitch));
            sb.AppendLine("InvDistance=" + S(InvDistance));
            sb.AppendLine("InvScale=" + S(InvScale));
            sb.AppendLine("FPShadowRes=" + FPShadowRes);
            sb.AppendLine("LightHysteresis=" + S(LightHysteresis));
            sb.AppendLine("WarmupShaders=" + (WarmupShaders ? "true" : "false"));
            sb.AppendLine("HudEveryFrames=" + HudEveryFrames);
            sb.AppendLine("DoorLog=" + (DoorLog ? "true" : "false"));
            sb.AppendLine("ClearUsedDoors=" + (ClearUsedDoors ? "true" : "false"));
            sb.AppendLine("OffHandNames=" + string.Join(",", OffHandNames));
            sb.AppendLine("OffHandScale=" + S(OffHandScale));
            sb.AppendLine("OffHandOffset=" + S(OffHandOffset.x) + "," + S(OffHandOffset.y) + "," + S(OffHandOffset.z));
            sb.AppendLine("OffHandAngles=" + S(OffHandAngles.x) + "," + S(OffHandAngles.y) + "," + S(OffHandAngles.z));
            sb.AppendLine("VRDrunkDrift=" + (VRDrunkDrift ? "true" : "false"));
            sb.AppendLine("WeaponVoxels=" + (WeaponVoxels ? "true" : "false"));
            sb.AppendLine("WeaponThickness=" + S(WeaponThickness));
            sb.AppendLine("ShowHands=" + (ShowHands ? "true" : "false"));
            sb.AppendLine("HandSize=" + S(HandSize.x) + "," + S(HandSize.y) + "," + S(HandSize.z));
            sb.AppendLine("CfgVersion=" + CfgVersion);
            File.WriteAllText(p, sb.ToString());
        }
        catch (Exception e) { Debug.LogError("[FirstPersonLoD] config save failed: " + e.Message); }
    }
}
