using System.Collections.Generic;
using UnityEngine;

// NEON SWARM — top-down 3D twin-stick wave survivor (Vampire Survivors-like).
// You only MOVE (drag = floating joystick, or WASD/arrows). Your weapon AUTO-FIRES at the
// nearest enemy. Endless swarms converge; killing them drops XP orbs that you vacuum up to
// fill the XP bar. Each LEVEL-UP freezes the world and you DRAFT 1 of 3 upgrades (rapid fire,
// multishot, pierce, orbit blades, magnet, vitality, damage, speed). Rapid kills stack a COMBO
// that ignites FRENZY (double fire-rate + screen tint). Survive as long as you can.
//
// 100% code-generated so it renders reliably in WebGL with engine-code stripping disabled:
//   * NO Rigidbody / NO colliders anywhere. Player, enemies, bullets, orbs, blades are all
//     pure Transform-driven; every interaction is a distance test. (coin-cruiser lesson.)
//   * Particles / juice go through Juice (CreatePrimitive-based, strip-safe).
//   * Default scene camera/light are stripped and rebuilt so we never double-light or shoot
//     the wrong camera (AutoShot reads Camera.main).
public class NeonSwarm : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__NeonSwarm");
        go.AddComponent<NeonSwarm>();
        DontDestroyOnLoad(go);
    }

    // ---- arena / tuning ----
    const float ARENA_R   = 40f;      // arena radius (neon boundary)
    const float PLAYER_R  = 0.62f;    // player collision radius
    const float MOVE_BASE = 8.2f;     // base move speed
    const float SPAWN_RING= 23f;      // enemies appear this far from the player (just offscreen)
    const float MAX_DT    = 0.05f;    // clamp huge frame steps
    const int   MAX_ENEMY = 150;      // hard cap (perf)

    enum State { Playing, LevelUp, Over }
    State state = State.Playing;

    // ---------------------------------------------------------------- entities
    class Enemy
    {
        public Transform tr; public int type; public float hp, maxhp, r, speed, dmg, xp;
        public float flash; public float spin; public Vector3 baseScale; public float bob;
    }
    class Bullet { public Transform tr; public Vector3 vel; public float life, dmg, r; public int pierce; public HashSet<Enemy> hitSet = new HashSet<Enemy>(); }
    class Orb    { public Transform tr; public float value; public float bob; }
    class Blade  { public Transform tr; }

    readonly List<Enemy> enemies = new List<Enemy>();
    readonly List<Bullet> bullets = new List<Bullet>();
    readonly List<Orb> orbs = new List<Orb>();
    readonly List<Blade> blades = new List<Blade>();

    // ---------------------------------------------------------------- scene refs
    Transform player, playerVisual, bladeRoot;
    Transform cam; Camera camComp;
    TextMesh hudScore, hudStat, comboText, banner, dbg, card0, card1, card2, cardHint;
    Transform hpBack, hpFill, xpBack, xpFill, flashQuad, dimPanel;
    Material enemyMat0, enemyMat1, enemyMat2, bulletMat, orbMat, bladeMat;

    // ---------------------------------------------------------------- run state
    float runTime;
    int score, best, kills, level;
    float hp = 100f, maxHP = 100f;
    float xp, xpNeed = 6f;

    // weapon / build
    float fireInterval = 0.55f, fireTimer;
    float bulletDamage = 9f, bulletSpeed = 22f, bulletLife = 1.3f, bulletR = 0.42f;
    int   projCount = 1, pierce = 0;
    float moveSpeed = MOVE_BASE, pickupR = 2.6f, bladeRadius = 2.5f;

    // combo / frenzy
    int combo; float comboTimer; bool frenzy; float comboFlash;

    // spawn director
    float spawnTimer, spawnInterval = 1.05f;

    // input (floating joystick)
    bool ptrDown, ptrWasDown; Vector2 ptrStart, ptrCur; Vector3 lastMoveDir = Vector3.forward;
    bool attract = true; float attractTimer; Vector3 attractDir = Vector3.forward;

    // level-up draft
    int[] draft = new int[3];

    // camera follow
    Vector3 camVel;

    // hud layout (aspect adaptive)
    float halfH = 6f, halfW = 9f, hudScale = 1f;
    float xpY, xpW, xpH, hpY, hpW, hpH;       // HUD bar geometry (set in AdjustHud)
    const float HUD_Z = 7f, FOV = 46f;

    bool showDbg; float fps; float damageFlash;

    // upgrade catalog
    struct Up { public string name, desc; public System.Func<bool> avail; public System.Action apply; }
    List<Up> ups;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        best = PlayerPrefs.GetInt("neon_best", 0);

        BuildEnvironment();
        BuildArena();
        BuildMaterials();
        BuildPlayer();
        BuildCamera();
        BuildHud();
        BuildUpgrades();
        ResetRun();
    }

    // ===================================================================== materials
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.4f, bool emissive = false, float emi = 0.7f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * emi);
        }
        return m;
    }

    void BuildMaterials()
    {
        enemyMat0 = Mat(new Color(1f, 0.22f, 0.45f), 0.0f, 0.35f, true, 0.8f);   // grunt  (hot pink)
        enemyMat1 = Mat(new Color(1f, 0.82f, 0.18f), 0.0f, 0.4f, true, 0.85f);   // darter (yellow)
        enemyMat2 = Mat(new Color(1f, 0.46f, 0.1f), 0.1f, 0.4f, true, 0.8f);     // brute  (orange)
        bulletMat = Mat(new Color(0.5f, 0.95f, 1f), 0f, 0.8f, true, 1.4f);       // bullet (cyan glow)
        orbMat    = Mat(new Color(0.4f, 1f, 0.6f), 0f, 0.85f, true, 1.2f);       // xp orb (green glow)
        bladeMat  = Mat(new Color(0.7f, 0.9f, 1f), 0.6f, 0.9f, true, 0.9f);      // orbit blade
    }

    static void NoCollide(GameObject g) { var c = g.GetComponent<Collider>(); if (c) Destroy(c); }

    // ===================================================================== environment
    void BuildEnvironment()
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(0.8f, 0.86f, 1f);
        sun.intensity = 0.92f;
        sun.transform.rotation = Quaternion.Euler(62f, 18f, 0f);
        sun.shadows = LightShadows.None;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.20f, 0.26f, 0.42f);
        RenderSettings.ambientEquatorColor = new Color(0.12f, 0.16f, 0.28f);
        RenderSettings.ambientGroundColor  = new Color(0.04f, 0.05f, 0.10f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.03f, 0.04f, 0.09f);
        RenderSettings.fogStartDistance = 26f;
        RenderSettings.fogEndDistance = 62f;
    }

    void BuildArena()
    {
        // neon grid floor
        var grid = MakeGridTex();
        var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(floor);
        floor.name = "Floor";
        floor.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        floor.transform.localScale = Vector3.one * (ARENA_R * 2.3f);
        var fm = Mat(new Color(0.06f, 0.08f, 0.14f), 0f, 0.2f, true, 1f);
        if (fm.HasProperty("_BaseMap")) fm.SetTexture("_BaseMap", grid);
        if (fm.HasProperty("_MainTex")) fm.SetTexture("_MainTex", grid);
        fm.mainTextureScale = new Vector2(ARENA_R * 0.55f, ARENA_R * 0.55f);
        if (fm.HasProperty("_EmissionMap")) fm.SetTexture("_EmissionMap", grid);
        if (fm.HasProperty("_EmissionColor")) fm.SetColor("_EmissionColor", new Color(0.10f, 0.45f, 0.6f));
        floor.GetComponent<Renderer>().sharedMaterial = fm;

        // glowing boundary ring (flat annulus)
        var ring = new GameObject("Boundary");
        ring.AddComponent<MeshFilter>().sharedMesh = RingMesh(ARENA_R - 0.6f, ARENA_R + 0.8f, 96);
        ring.AddComponent<MeshRenderer>().sharedMaterial = Mat(new Color(0.3f, 0.9f, 1f), 0f, 0.7f, true, 1.6f);
        ring.transform.position = new Vector3(0f, 0.04f, 0f);
        // a dimmer inner ring for depth
        var ring2 = new GameObject("Boundary2");
        ring2.AddComponent<MeshFilter>().sharedMesh = RingMesh(ARENA_R - 3f, ARENA_R - 2.7f, 96);
        ring2.AddComponent<MeshRenderer>().sharedMaterial = Mat(new Color(0.2f, 0.5f, 0.7f), 0f, 0.6f, true, 0.8f);
        ring2.transform.position = new Vector3(0f, 0.03f, 0f);
    }

    static Texture2D MakeGridTex()
    {
        int S = 128; var t = new Texture2D(S, S);
        var bg = new Color(0.05f, 0.07f, 0.13f);
        var line = new Color(0.18f, 0.6f, 0.78f);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                bool g = (x == 0 || y == 0 || x == 1 || y == 1);
                t.SetPixel(x, y, g ? line : bg);
            }
        t.Apply(); t.wrapMode = TextureWrapMode.Repeat; t.filterMode = FilterMode.Bilinear;
        return t;
    }

    static Mesh RingMesh(float inner, float outer, int seg)
    {
        var v = new Vector3[seg * 2]; var tri = new int[seg * 6];
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg, c = Mathf.Cos(a), s = Mathf.Sin(a);
            v[i * 2]     = new Vector3(c * outer, 0f, s * outer);
            v[i * 2 + 1] = new Vector3(c * inner, 0f, s * inner);
        }
        for (int i = 0; i < seg; i++)
        {
            int a = i * 2, b = ((i + 1) % seg) * 2, t = i * 6;
            tri[t] = a; tri[t + 1] = a + 1; tri[t + 2] = b;
            tri[t + 3] = b; tri[t + 4] = a + 1; tri[t + 5] = b + 1;
        }
        var m = new Mesh(); m.vertices = v; m.triangles = tri; m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }

    // ===================================================================== player
    void BuildPlayer()
    {
        player = new GameObject("Player").transform;
        playerVisual = new GameObject("PlayerVisual").transform;
        playerVisual.SetParent(player, false);

        // body: bright capsule core + ring fins for a "drone" silhouette
        var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        NoCollide(core); core.transform.SetParent(playerVisual, false);
        core.transform.localScale = new Vector3(0.95f, 0.7f, 0.95f);
        core.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.6f, 0.95f, 1f), 0.2f, 0.85f, true, 1.1f);

        var fin = GameObject.CreatePrimitive(PrimitiveType.Cube);
        NoCollide(fin); fin.transform.SetParent(playerVisual, false);
        fin.transform.localScale = new Vector3(1.7f, 0.18f, 0.34f);
        fin.transform.localPosition = new Vector3(0f, 0.1f, 0f);
        fin.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.25f, 0.7f, 1f), 0.3f, 0.7f, true, 0.6f);

        var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
        NoCollide(nose); nose.transform.SetParent(playerVisual, false);
        nose.transform.localScale = new Vector3(0.28f, 0.22f, 1.1f);
        nose.transform.localPosition = new Vector3(0f, 0.1f, 0.55f);
        nose.GetComponent<Renderer>().sharedMaterial = Mat(new Color(1f, 1f, 1f), 0.2f, 0.9f, true, 1.0f);

        player.position = new Vector3(0f, 0.55f, 0f);

        bladeRoot = new GameObject("BladeRoot").transform;
        bladeRoot.SetParent(player, false);
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.02f, 0.03f, 0.07f);
        camComp.fieldOfView = FOV;
        camComp.farClipPlane = 120f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
        cam.position = new Vector3(0f, 20f, -12f);
        cam.rotation = Quaternion.Euler(58f, 0f, 0f);
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor, TextAlignment align = TextAlignment.Center)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = align;
        t.transform.SetParent(cam, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    Transform MakeBar(Color c, float emi)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(q);
        q.GetComponent<Renderer>().sharedMaterial = Mat(c, 0f, 0.5f, true, emi);
        q.transform.SetParent(cam, false);
        q.transform.localRotation = Quaternion.identity;
        return q.transform;
    }

    void BuildHud()
    {
        hudScore = MakeText(0.085f, Color.white, TextAnchor.UpperLeft, TextAlignment.Left);
        hudStat  = MakeText(0.060f, new Color(0.8f, 0.95f, 1f), TextAnchor.UpperRight, TextAlignment.Right);
        comboText = MakeText(0.14f, new Color(1f, 0.85f, 0.3f), TextAnchor.MiddleCenter);
        banner   = MakeText(0.13f, Color.white, TextAnchor.MiddleCenter);
        dbg      = MakeText(0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft, TextAlignment.Left);
        dbg.gameObject.SetActive(false);

        card0 = MakeText(0.055f, Color.white, TextAnchor.MiddleCenter);
        card1 = MakeText(0.055f, Color.white, TextAnchor.MiddleCenter);
        card2 = MakeText(0.055f, Color.white, TextAnchor.MiddleCenter);
        cardHint = MakeText(0.06f, new Color(0.9f, 0.95f, 1f), TextAnchor.MiddleCenter);

        hpBack = MakeBar(new Color(0.2f, 0.04f, 0.08f), 0.2f);
        hpFill = MakeBar(new Color(1f, 0.3f, 0.4f), 0.7f);
        xpBack = MakeBar(new Color(0.05f, 0.12f, 0.1f), 0.2f);
        xpFill = MakeBar(new Color(0.4f, 1f, 0.6f), 0.7f);

        // full-screen damage flash quad — alpha-blended (Sprites/Default), rendered BEHIND the
        // HUD text (farther than HUD_Z) so it tints the world but never the readouts.
        var f = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(f);
        var fsh = Shader.Find("Sprites/Default"); if (fsh == null) fsh = Shader.Find("Unlit/Transparent");
        var fm = new Material(fsh) { color = new Color(1f, 0.12f, 0.16f, 0f) };
        f.GetComponent<Renderer>().sharedMaterial = fm;
        f.transform.SetParent(cam, false);
        f.transform.localPosition = new Vector3(0f, 0f, HUD_Z + 1.5f);
        f.transform.localScale = new Vector3(90f, 60f, 1f);
        flashQuad = f.transform;
        SetAlpha(flashQuad, 0f);

        // dim panel shown behind the level-up cards (and faintly on game over)
        var dp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(dp);
        var dsh = Shader.Find("Sprites/Default"); if (dsh == null) dsh = Shader.Find("Unlit/Transparent");
        dp.GetComponent<Renderer>().sharedMaterial = new Material(dsh) { color = new Color(0.02f, 0.03f, 0.07f, 0f) };
        dp.transform.SetParent(cam, false);
        dp.transform.localPosition = new Vector3(0f, 0f, HUD_Z + 1.0f);
        dp.transform.localScale = new Vector3(90f, 60f, 1f);
        dimPanel = dp.transform;

        comboText.text = ""; banner.text = "";
        AdjustHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.35f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF = 7.0f;
        hudScale = Mathf.Clamp(halfW / REF, 0.34f, 1.25f);
        float ix = halfW * 0.95f;

        // XP bar: thin strip across the very top. HP bar: thin strip across the bottom.
        xpW = halfW * 1.94f; xpH = halfH * 0.030f; xpY = halfH * 0.965f;
        hpW = halfW * 1.5f;  hpH = halfH * 0.038f; hpY = -halfH * 0.955f;
        PlaceBar(xpBack, 0f, xpY, xpW, xpH);
        PlaceBar(hpBack, 0f, hpY, hpW, hpH);

        float topY = halfH * 0.85f;            // score/stat sit just under the XP strip
        hudScore.transform.localPosition = new Vector3(-ix, topY, HUD_Z); hudScore.characterSize = 0.080f * hudScale;
        hudStat.transform.localPosition  = new Vector3( ix, topY, HUD_Z); hudStat.characterSize  = 0.056f * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -halfH * 0.45f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
        comboText.transform.localPosition = new Vector3(0f, halfH * 0.46f, HUD_Z);
        banner.transform.localPosition    = new Vector3(0f, halfH * 0.22f, HUD_Z); banner.characterSize = 0.095f * hudScale;

        // level-up cards: 3 columns on wide screens, stacked vertically on narrow (portrait)
        bool portrait = aspect < 0.95f;
        if (portrait)
        {
            card0.transform.localPosition = new Vector3(0f, halfH * 0.30f, HUD_Z);
            card1.transform.localPosition = new Vector3(0f, halfH * 0.02f, HUD_Z);
            card2.transform.localPosition = new Vector3(0f, -halfH * 0.26f, HUD_Z);
        }
        else
        {
            card0.transform.localPosition = new Vector3(-halfW * 0.62f, halfH * 0.04f, HUD_Z);
            card1.transform.localPosition = new Vector3(0f, halfH * 0.04f, HUD_Z);
            card2.transform.localPosition = new Vector3( halfW * 0.62f, halfH * 0.04f, HUD_Z);
        }
        cardHint.transform.localPosition = new Vector3(0f, -halfH * 0.6f, HUD_Z); cardHint.characterSize = 0.052f * hudScale;
        float cs = (portrait ? 0.044f : 0.05f) * hudScale;
        card0.characterSize = cs; card1.characterSize = cs; card2.characterSize = cs;
    }

    void PlaceBar(Transform b, float cx, float cy, float w, float h)
    {
        b.localPosition = new Vector3(cx, cy, HUD_Z);
        b.localScale = new Vector3(w, h, 1f);
    }

    void RefreshBars()
    {
        float hpFrac = Mathf.Clamp01(hp / maxHP);
        float xpFrac = Mathf.Clamp01(xp / xpNeed);
        // left-anchored fills (slightly nearer the camera so they sit on top of the backs)
        hpFill.localScale = new Vector3(hpW * hpFrac, hpH * 0.85f, 1f);
        hpFill.localPosition = new Vector3(-hpW * 0.5f + hpW * hpFrac * 0.5f, hpY, HUD_Z - 0.02f);
        xpFill.localScale = new Vector3(xpW * xpFrac, xpH * 0.85f, 1f);
        xpFill.localPosition = new Vector3(-xpW * 0.5f + xpW * xpFrac * 0.5f, xpY, HUD_Z - 0.02f);
    }

    void SetAlpha(Transform t, float a)
    {
        var r = t.GetComponent<Renderer>();
        if (r == null) return;
        var c = r.sharedMaterial.color; c.a = a; r.sharedMaterial.color = c;
    }

    // ===================================================================== upgrades
    void BuildUpgrades()
    {
        ups = new List<Up>
        {
            new Up{ name="RAPID FIRE", desc="fire 18% faster", avail=()=>fireInterval>0.14f,
                    apply=()=>fireInterval*=0.82f },
            new Up{ name="MULTISHOT", desc="+1 projectile", avail=()=>projCount<9,
                    apply=()=>projCount++ },
            new Up{ name="POWER SHOT", desc="+6 bullet damage", avail=()=>true,
                    apply=()=>bulletDamage+=6f },
            new Up{ name="PIERCE", desc="bullets pass +1 foe", avail=()=>pierce<6,
                    apply=()=>pierce++ },
            new Up{ name="ORBIT BLADE", desc="+1 spinning blade", avail=()=>blades.Count<6,
                    apply=AddBlade },
            new Up{ name="MAGNET", desc="+45% pickup range", avail=()=>pickupR<12f,
                    apply=()=>pickupR*=1.45f },
            new Up{ name="OVERDRIVE", desc="+1.4 move speed", avail=()=>moveSpeed<18f,
                    apply=()=>moveSpeed+=1.4f },
            new Up{ name="VITALITY", desc="+25 max HP, heal", avail=()=>true,
                    apply=()=>{ maxHP+=25f; hp=Mathf.Min(maxHP,hp+25f); } },
        };
    }

    void AddBlade()
    {
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        NoCollide(b);
        b.transform.SetParent(bladeRoot, false);
        b.transform.localScale = new Vector3(0.9f, 0.18f, 0.32f);
        b.GetComponent<Renderer>().sharedMaterial = bladeMat;
        blades.Add(new Blade { tr = b.transform });
    }

    // ===================================================================== run reset
    void ResetRun()
    {
        foreach (var e in enemies) if (e.tr) Destroy(e.tr.gameObject); enemies.Clear();
        foreach (var b in bullets) if (b.tr) Destroy(b.tr.gameObject); bullets.Clear();
        foreach (var o in orbs) if (o.tr) Destroy(o.tr.gameObject); orbs.Clear();
        foreach (var bl in blades) if (bl.tr) Destroy(bl.tr.gameObject); blades.Clear();

        state = State.Playing;
        runTime = 0f; score = 0; kills = 0; level = 1;
        maxHP = 100f; hp = 100f; xp = 0f; xpNeed = 6f;
        fireInterval = 0.55f; fireTimer = 0f; bulletDamage = 9f; projCount = 1; pierce = 0;
        moveSpeed = MOVE_BASE; pickupR = 2.6f;
        combo = 0; comboTimer = 0f; frenzy = false; comboFlash = 0f;
        spawnTimer = 0f; spawnInterval = 1.05f;
        player.position = new Vector3(0f, 0.55f, 0f);
        cam.position = new Vector3(0f, 20f, -12f);
        AddBlade();                                   // start with a single guard blade
        attract = true; attractTimer = 0f;
        SetAlpha(flashQuad, 0f); damageFlash = 0f;
        comboText.text = ""; banner.text = "";
        hudScore.gameObject.SetActive(true); hudStat.gameObject.SetActive(true);
        SetCards(false);
        RefreshHud();
    }

    // ===================================================================== update
    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, MAX_DT);
        fps = Mathf.Lerp(fps, 1f / Mathf.Max(0.0001f, Time.deltaTime), 0.1f);

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        ReadPointer();

        if (state == State.Playing) UpdatePlaying(dt);
        else if (state == State.LevelUp) UpdateLevelUp();
        else if (state == State.Over) UpdateOver();

        UpdateCamera(dt);
        UpdateVisualFx(dt);
        ptrWasDown = ptrDown;
    }

    // ---------------------------------------------------------------- input
    void ReadPointer()
    {
        ptrDown = false; ptrCur = Vector2.zero;
        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            ptrDown = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
            ptrCur = t.position;
        }
        else if (Input.GetMouseButton(0))
        {
            ptrDown = true; ptrCur = (Vector2)Input.mousePosition;
        }
        else if (Input.GetMouseButtonUp(0))
        {
            ptrCur = (Vector2)Input.mousePosition;
        }
        if (ptrDown && !ptrWasDown) ptrStart = ptrCur;    // begin floating joystick
    }

    Vector3 MoveInput()
    {
        // keyboard
        float kx = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f)
                 - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
        float kz = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f)
                 - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
        Vector3 kv = new Vector3(kx, 0f, kz);
        if (kv.sqrMagnitude > 0.01f) { attract = false; return Vector3.ClampMagnitude(kv, 1f); }

        // floating joystick (drag)
        if (ptrDown)
        {
            Vector2 d = ptrCur - ptrStart;
            float ref0 = Mathf.Min(Screen.width, Screen.height) * 0.18f;     // joystick reach
            Vector3 v = new Vector3(d.x, 0f, d.y) / Mathf.Max(1f, ref0);
            if (v.sqrMagnitude > 0.0009f) { attract = false; return Vector3.ClampMagnitude(v, 1f); }
        }
        return Vector3.zero;
    }

    // ---------------------------------------------------------------- playing
    void UpdatePlaying(float dt)
    {
        runTime += dt;

        // movement
        Vector3 mv = MoveInput();
        if (attract) { mv = AttractMove(dt); }
        if (mv.sqrMagnitude > 0.0001f)
        {
            player.position += mv * moveSpeed * dt;
            lastMoveDir = mv.normalized;
            // clamp to arena
            Vector3 p = player.position; p.y = 0.55f;
            Vector2 fl = new Vector2(p.x, p.z);
            if (fl.magnitude > ARENA_R - 1.2f) { fl = fl.normalized * (ARENA_R - 1.2f); p.x = fl.x; p.z = fl.y; }
            player.position = p;
        }
        // bank the ship toward movement
        if (lastMoveDir.sqrMagnitude > 0.01f)
        {
            Quaternion want = Quaternion.LookRotation(new Vector3(lastMoveDir.x, 0f, lastMoveDir.z), Vector3.up);
            playerVisual.rotation = Quaternion.Slerp(playerVisual.rotation, want, dt * 10f);
        }

        // weapon
        fireTimer -= dt;
        float fi = fireInterval * (frenzy ? 0.5f : 1f);
        if (fireTimer <= 0f)
        {
            if (TryFire()) fireTimer = fi; else fireTimer = 0.12f;
        }

        UpdateBullets(dt);
        UpdateEnemies(dt);
        UpdateOrbs(dt);
        UpdateBlades(dt);
        SpawnDirector(dt);

        // combo decay
        if (comboTimer > 0f) { comboTimer -= dt; if (comboTimer <= 0f) { combo = 0; frenzy = false; } }
        frenzy = combo >= 12;

        RefreshHud();

        if (hp <= 0f) GameOver();
    }

    Vector3 AttractMove(float dt)
    {
        // idle demo: flee the nearest enemy while drifting, so the screen stays lively
        attractTimer -= dt;
        Enemy near = NearestEnemy(player.position, out float nd);
        Vector3 flee = Vector3.zero;
        if (near != null && nd < 9f)
            flee = (player.position - near.tr.position); flee.y = 0f;
        if (attractTimer <= 0f) { attractTimer = Random.Range(0.6f, 1.4f); attractDir = Random.insideUnitSphere; attractDir.y = 0f; }
        Vector3 dir = flee.normalized * 1.4f + attractDir.normalized * 0.7f;
        // stay off the wall
        Vector2 fl = new Vector2(player.position.x, player.position.z);
        if (fl.magnitude > ARENA_R - 8f) dir += new Vector3(-fl.x, 0f, -fl.y).normalized * 1.5f;
        dir.y = 0f;
        return dir.sqrMagnitude > 0.001f ? Vector3.ClampMagnitude(dir, 1f) : Vector3.zero;
    }

    // ---------------------------------------------------------------- weapon
    bool TryFire()
    {
        Enemy target = NearestEnemy(player.position, out float d);
        if (target == null) return false;
        Vector3 aim = (target.tr.position - player.position); aim.y = 0f;
        if (aim.sqrMagnitude < 0.0001f) aim = lastMoveDir;
        aim.Normalize();
        float baseAng = Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg;
        float spread = projCount > 1 ? Mathf.Min(11f * (projCount - 1), 44f) : 0f;
        for (int i = 0; i < projCount; i++)
        {
            float t = projCount == 1 ? 0f : (i / (float)(projCount - 1) - 0.5f);
            float ang = baseAng + t * spread;
            float rad = ang * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            SpawnBullet(dir);
        }
        Juice.Blip(frenzy ? 1100f : 760f, 0.05f, 0.18f);
        // muzzle bank
        return true;
    }

    void SpawnBullet(Vector3 dir)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        NoCollide(g);
        g.transform.localScale = new Vector3(0.35f, 0.35f, 0.75f);
        g.transform.position = player.position + dir * 0.7f;
        g.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        g.GetComponent<Renderer>().sharedMaterial = bulletMat;
        bullets.Add(new Bullet { tr = g.transform, vel = dir * bulletSpeed, life = bulletLife, dmg = bulletDamage, r = bulletR, pierce = pierce });
    }

    void UpdateBullets(float dt)
    {
        for (int i = bullets.Count - 1; i >= 0; i--)
        {
            var b = bullets[i];
            b.tr.position += b.vel * dt;
            b.life -= dt;
            bool dead = b.life <= 0f;
            if (!dead)
            {
                for (int j = enemies.Count - 1; j >= 0; j--)
                {
                    var e = enemies[j];
                    if (b.hitSet.Contains(e)) continue;
                    float rr = b.r + e.r;
                    if ((e.tr.position - b.tr.position).sqrMagnitude <= rr * rr)
                    {
                        DamageEnemy(e, b.dmg, b.vel.normalized);
                        b.hitSet.Add(e);
                        if (b.pierce-- <= 0) { dead = true; break; }
                    }
                }
            }
            if (dead) { Destroy(b.tr.gameObject); bullets.RemoveAt(i); }
        }
    }

    // ---------------------------------------------------------------- enemies
    Enemy NearestEnemy(Vector3 from, out float dist)
    {
        Enemy best = null; float bd = float.MaxValue;
        for (int i = 0; i < enemies.Count; i++)
        {
            float d = (enemies[i].tr.position - from).sqrMagnitude;
            if (d < bd) { bd = d; best = enemies[i]; }
        }
        dist = best != null ? Mathf.Sqrt(bd) : float.MaxValue;
        return best;
    }

    void SpawnDirector(float dt)
    {
        // ramp difficulty: spawn faster over time, cap density
        float mins = runTime / 60f;
        spawnInterval = Mathf.Max(0.14f, 0.82f - mins * 0.34f);
        int batch = 1 + Mathf.FloorToInt(mins * 2.0f);

        spawnTimer -= dt;
        if (spawnTimer <= 0f && enemies.Count < MAX_ENEMY)
        {
            spawnTimer = spawnInterval;
            for (int i = 0; i < batch && enemies.Count < MAX_ENEMY; i++) SpawnEnemy(mins);
        }
    }

    void SpawnEnemy(float mins)
    {
        // choose type by elapsed time
        int type = 0;
        float roll = Random.value;
        if (mins > 0.9f && roll < 0.32f) type = 1;          // darter
        if (mins > 1.7f && roll > 0.86f) type = 2;          // brute
        float hpScale = 1f + mins * 0.55f;

        var e = new Enemy { type = type };
        Color col; PrimitiveType shape; Material mat;
        if (type == 1)      { e.maxhp = 8f; e.r = 0.45f; e.speed = 5.6f; e.dmg = 8f; e.xp = 1f; shape = PrimitiveType.Sphere; mat = enemyMat1; col = default; }
        else if (type == 2) { e.maxhp = 70f; e.r = 1.05f; e.speed = 2.0f; e.dmg = 22f; e.xp = 5f; shape = PrimitiveType.Cube; mat = enemyMat2; col = default; }
        else                { e.maxhp = 15f; e.r = 0.6f; e.speed = 3.3f; e.dmg = 10f; e.xp = 1f; shape = PrimitiveType.Cube; mat = enemyMat0; col = default; }
        e.maxhp *= hpScale; e.hp = e.maxhp;

        var g = GameObject.CreatePrimitive(shape);
        NoCollide(g);
        float s = e.r * 2f;
        e.baseScale = (type == 2) ? new Vector3(s, s * 0.9f, s) : new Vector3(s, s, s);
        g.transform.localScale = e.baseScale;
        g.GetComponent<Renderer>().sharedMaterial = mat;

        // spawn on a ring around the player, kept inside the arena
        float a = Random.value * Mathf.PI * 2f;
        Vector3 pos = player.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * SPAWN_RING;
        Vector2 fl = new Vector2(pos.x, pos.z);
        if (fl.magnitude > ARENA_R - 1.5f) fl = fl.normalized * (ARENA_R - 1.5f);
        pos = new Vector3(fl.x, 0.55f + (type == 2 ? 0.2f : 0f), fl.y);
        g.transform.position = pos;
        e.tr = g.transform;
        e.spin = Random.Range(40f, 120f) * (Random.value < 0.5f ? -1f : 1f);
        e.bob = Random.value * 6f;
        enemies.Add(e);
    }

    void UpdateEnemies(float dt)
    {
        int n = enemies.Count;
        // pairwise separation so the swarm spreads out instead of stacking into a blob
        for (int i = 0; i < n; i++)
        {
            var a = enemies[i];
            for (int j = i + 1; j < n; j++)
            {
                var b = enemies[j];
                Vector3 d = a.tr.position - b.tr.position; d.y = 0f;
                float min = a.r + b.r; float sq = d.sqrMagnitude;
                if (sq < min * min && sq > 0.0001f)
                {
                    float dist = Mathf.Sqrt(sq);
                    Vector3 push = d * ((min - dist) / dist * 0.5f);
                    a.tr.position += push; b.tr.position -= push;
                }
            }
        }

        float dmgAccum = 0f;
        for (int i = enemies.Count - 1; i >= 0; i--)
        {
            var e = enemies[i];
            Vector3 to = player.position - e.tr.position; to.y = 0f;
            float d = to.magnitude;
            Vector3 dir = d > 0.001f ? to / d : Vector3.forward;

            e.tr.position += dir * e.speed * dt;

            // spin + bob for life
            e.bob += dt * 3f;
            e.tr.rotation = Quaternion.Euler(0f, e.tr.eulerAngles.y + e.spin * dt, 0f);

            // hit flash (scale punch)
            if (e.flash > 0f)
            {
                e.flash -= dt;
                float k = 1f + Mathf.Max(0f, e.flash) * 2.2f;
                e.tr.localScale = e.baseScale * k;
            }
            else e.tr.localScale = e.baseScale;

            // contact damage to player
            float rr = e.r + PLAYER_R;
            if (d <= rr)
            {
                dmgAccum += e.dmg * dt;
                // soft shove apart
                e.tr.position -= dir * (rr - d) * 0.5f;
            }
        }
        if (dmgAccum > 0f && state == State.Playing)
        {
            hp -= dmgAccum;
            damageFlash = Mathf.Min(0.38f, damageFlash + dmgAccum * 0.04f);
            if (Time.frameCount % 12 == 0) Juice.Shake(0.18f);
        }
    }

    void DamageEnemy(Enemy e, float dmg, Vector3 fromDir)
    {
        e.hp -= dmg;
        e.flash = 0.09f;
        e.tr.position += fromDir * 0.12f;             // knockback nudge
        if (e.hp <= 0f) KillEnemy(e);
    }

    void KillEnemy(Enemy e)
    {
        Color c = e.type == 1 ? new Color(1f, 0.85f, 0.2f) : e.type == 2 ? new Color(1f, 0.5f, 0.12f) : new Color(1f, 0.3f, 0.45f);
        Juice.Pop(e.tr.position, c, e.type == 2 ? 16 : 9);
        Juice.Blip(e.type == 2 ? 240f : 520f, 0.07f, 0.22f);
        SpawnOrb(e.tr.position, e.xp);
        kills++;
        score += e.type == 2 ? 25 : 8;
        // combo
        combo++; comboTimer = 2.2f; comboFlash = 1f;
        if (e.type == 2) Juice.Shake(0.3f);
        enemies.Remove(e);
        if (e.tr) Destroy(e.tr.gameObject);
    }

    // ---------------------------------------------------------------- orbs / xp
    void SpawnOrb(Vector3 pos, float value)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        NoCollide(g);
        g.transform.localScale = Vector3.one * (value >= 5f ? 0.6f : 0.38f);
        g.transform.position = new Vector3(pos.x, 0.5f, pos.z);
        g.GetComponent<Renderer>().sharedMaterial = orbMat;
        orbs.Add(new Orb { tr = g.transform, value = value, bob = Random.value * 6f });
    }

    void UpdateOrbs(float dt)
    {
        for (int i = orbs.Count - 1; i >= 0; i--)
        {
            var o = orbs[i];
            Vector3 to = player.position - o.tr.position; to.y = 0f;
            float d = to.magnitude;
            o.bob += dt * 4f;
            o.tr.position += new Vector3(0f, Mathf.Sin(o.bob) * 0.003f, 0f);
            if (d < pickupR)
            {
                // vacuum in
                o.tr.position += to.normalized * Mathf.Max(7f, (pickupR - d) * 14f) * dt;
                if (d < 0.75f)
                {
                    GainXp(o.value);
                    Destroy(o.tr.gameObject); orbs.RemoveAt(i); continue;
                }
            }
            o.tr.Rotate(0f, 90f * dt, 0f);
        }
    }

    void GainXp(float v)
    {
        xp += v; score += Mathf.RoundToInt(v * 2f);
        Juice.Blip(980f, 0.04f, 0.12f);
        if (xp >= xpNeed) LevelUp();
    }

    void LevelUp()
    {
        xp -= xpNeed;
        level++;
        xpNeed = 6f + level * 4.2f;
        Juice.Score(player.position);
        Juice.Shake(0.25f);
        // pick 3 distinct available upgrades
        var pool = new List<int>();
        for (int i = 0; i < ups.Count; i++) if (ups[i].avail()) pool.Add(i);
        // shuffle
        for (int i = pool.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); (pool[i], pool[j]) = (pool[j], pool[i]); }
        int n = Mathf.Min(3, pool.Count);
        for (int i = 0; i < 3; i++) draft[i] = i < n ? pool[i] : -1;
        ShowCards();
        lvAutoTimer = 1.3f;          // attract demo auto-drafts after this delay
        state = State.LevelUp;
    }

    // ---------------------------------------------------------------- blades
    void UpdateBlades(float dt)
    {
        if (blades.Count == 0) return;
        bladeRoot.Rotate(0f, 200f * dt, 0f);
        int n = blades.Count;
        for (int i = 0; i < n; i++)
        {
            float a = (i / (float)n) * Mathf.PI * 2f;
            Vector3 lp = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * bladeRadius;
            blades[i].tr.localPosition = lp;
            blades[i].tr.localRotation = Quaternion.LookRotation(new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)), Vector3.up);
            // contact damage
            Vector3 wp = blades[i].tr.position;
            for (int j = enemies.Count - 1; j >= 0; j--)
            {
                var e = enemies[j];
                float rr = e.r + 0.7f;
                if ((e.tr.position - wp).sqrMagnitude <= rr * rr)
                    DamageEnemy(e, 22f * dt, (e.tr.position - player.position).normalized);
            }
        }
    }

    // ---------------------------------------------------------------- level-up modal
    void SetCards(bool on)
    {
        card0.gameObject.SetActive(on); card1.gameObject.SetActive(on);
        card2.gameObject.SetActive(on); cardHint.gameObject.SetActive(on);
        if (dimPanel) SetAlpha(dimPanel, on ? 0.62f : 0f);
    }

    void ShowCards()
    {
        SetCards(true);
        TextMesh[] cs = { card0, card1, card2 };
        for (int i = 0; i < 3; i++)
        {
            if (draft[i] >= 0) { var u = ups[draft[i]]; cs[i].text = "[" + (i + 1) + "]  " + u.name + "\n" + u.desc; }
            else cs[i].text = "";
        }
        cardHint.text = "LEVEL " + level + " UP!\ntap a card  ·  1 / 2 / 3";
        banner.text = "";
    }

    float lvAutoTimer;

    void UpdateLevelUp()
    {
        int pick = -1; bool human = false;
        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) { pick = 0; human = true; }
        else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) { pick = 1; human = true; }
        else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) { pick = 2; human = true; }
        else if (ptrDown && !ptrWasDown)
        {
            human = true;
            if (camComp != null && camComp.aspect < 0.95f)
            {
                float fy = 1f - ptrCur.y / Mathf.Max(1f, Screen.height);   // 0=top, 1=bottom (cards stacked)
                pick = fy < 0.42f ? 0 : fy < 0.57f ? 1 : 2;                // boundaries between stacked cards
            }
            else
            {
                float fx = ptrCur.x / Mathf.Max(1f, Screen.width);          // landscape: pick by column
                pick = fx < 0.34f ? 0 : fx < 0.67f ? 1 : 2;
            }
        }
        if (human) attract = false;
        // attract demo: auto-draft after a beat so the idle loop keeps flowing
        if (pick < 0 && attract)
        {
            lvAutoTimer -= Time.deltaTime;
            if (lvAutoTimer <= 0f)
                for (int t = 0; t < 6 && pick < 0; t++) { int k = Random.Range(0, 3); if (draft[k] >= 0) pick = k; }
        }
        if (pick >= 0 && draft[pick] >= 0)
        {
            ups[draft[pick]].apply();
            Juice.Blip(900f, 0.08f, 0.3f); Juice.Blip(1350f, 0.07f, 0.22f);
            SetCards(false);
            state = State.Playing;
            // recompute frenzy threshold display etc. (handled live)
        }
    }

    // ---------------------------------------------------------------- game over
    void GameOver()
    {
        hp = 0f;
        state = State.Over;
        if (score > best) { best = score; PlayerPrefs.SetInt("neon_best", best); PlayerPrefs.Save(); }
        Juice.Lose();
        hudScore.gameObject.SetActive(false); hudStat.gameObject.SetActive(false);
        if (dimPanel) SetAlpha(dimPanel, 0.5f);
        banner.text = "GAME OVER\n\nTIME  " + FmtTime(runTime) + "\nLEVEL  " + level + "\nSCORE  " + score + "\nBEST  " + best + "\n\nTAP / R  to retry";
    }

    void UpdateOver()
    {
        if (Input.GetKeyDown(KeyCode.R) || (ptrDown && !ptrWasDown) || Input.GetKeyDown(KeyCode.Space))
            ResetRun();
    }

    // ---------------------------------------------------------------- camera
    void UpdateCamera(float dt)
    {
        if (camComp == null) return;
        AdjustHud();
        float aspect = Mathf.Max(0.35f, camComp.aspect);
        // portrait is narrower -> pull camera back so the same play area is visible
        float zoom = Mathf.Clamp(1.45f / aspect, 0.95f, 1.7f);
        Vector3 want = player.position + new Vector3(0f, 20f * zoom, -12f * zoom);
        cam.position = Vector3.SmoothDamp(cam.position, want, ref camVel, 0.12f);
        cam.rotation = Quaternion.Euler(58f, 0f, 0f);
    }

    // ---------------------------------------------------------------- fx + hud text
    void UpdateVisualFx(float dt)
    {
        // damage vignette fade
        damageFlash = Mathf.Max(0f, damageFlash - dt * 1.6f);
        SetAlpha(flashQuad, Mathf.Clamp01(damageFlash));

        // combo text
        if (comboFlash > 0f) comboFlash = Mathf.Max(0f, comboFlash - dt * 2.5f);
        if (combo >= 3 && state == State.Playing)
        {
            comboText.text = (frenzy ? "FRENZY  x" : "COMBO  x") + combo;
            comboText.color = frenzy ? new Color(1f, 0.4f, 0.5f) : new Color(1f, 0.85f, 0.3f);
            comboText.characterSize = (0.10f + comboFlash * 0.04f) * hudScale;
        }
        else comboText.text = "";

        // subtle player bob
        if (playerVisual) playerVisual.localPosition = new Vector3(0f, Mathf.Sin(Time.time * 3f) * 0.05f, 0f);

        if (showDbg && dbg)
            dbg.text = string.Format("fps {0:00}  state {1}  enemies {2}  bullets {3}  orbs {4}\nlv {5} xp {6:0}/{7:0} hp {8:0}/{9:0}\nfire {10:0.00} proj {11} pierce {12} blades {13} spd {14:0.0} pick {15:0.0}\ncombo {16} frenzy {17} spawnInt {18:0.00}",
                fps, state, enemies.Count, bullets.Count, orbs.Count,
                level, xp, xpNeed, hp, maxHP,
                fireInterval, projCount, pierce, blades.Count, moveSpeed, pickupR,
                combo, frenzy, spawnInterval);

        RefreshBars();
    }

    void RefreshHud()
    {
        if (hudScore) hudScore.text = "SCORE  " + score + "\nLV " + level;
        if (hudStat) hudStat.text = FmtTime(runTime) + "\nKILLS " + kills + "\nBEST " + best;
    }

    static string FmtTime(float t)
    {
        int m = (int)(t / 60f), s = (int)(t % 60f);
        return string.Format("{0}:{1:00}", m, s);
    }
}
