using System;
using System.Collections.Generic;
using System.Reflection;
using COIJointVentures.Networking.Protocol;
using UnityEngine;

namespace COIJointVentures.Waypoints;

internal sealed class WaypointManager
{
    private const int MaxWaypoints = 5;
    private const float Lifetime = 7f;
    private const float FadeStart = 5f;
    private const float BaseScale = 1f;
    private const float ScalePerUnit = 0.012f; // how much to grow per unit of distance

    private static readonly Color[] PeerColors =
    {
        new Color(0.2f, 0.6f, 1.0f),  // blue
        new Color(1.0f, 0.35f, 0.3f), // red
        new Color(0.3f, 0.9f, 0.4f),  // green
        new Color(1.0f, 0.75f, 0.2f), // yellow
        new Color(0.8f, 0.4f, 1.0f),  // purple
        new Color(1.0f, 0.55f, 0.1f), // orange
    };

    private readonly List<ActiveWaypoint> _active = new();
    private GameObject? _audioGo;
    private object? _audioSource;  // AudioSource via reflection
    private object? _pingClip;    // AudioClip via reflection
    private MethodInfo? _playOneShotMethod;

    public void Spawn(WaypointPayload wp)
    {
        // enforce cap — kill the oldest
        while (_active.Count >= MaxWaypoints)
        {
            DestroyWaypoint(_active[0]);
            _active.RemoveAt(0);
        }

        var color = PeerColors[Mathf.Abs(wp.ColorIndex) % PeerColors.Length];
        var pos = new Vector3(wp.X, wp.Y, wp.Z);

        var root = new GameObject($"Waypoint_{wp.SenderName}");
        root.transform.position = pos;

        // ground ring — flat cylinder
        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        UnityEngine.Object.Destroy(ring.GetComponent<Collider>());
        ring.transform.SetParent(root.transform, false);
        ring.transform.localPosition = new Vector3(0f, 0.05f, 0f);
        ring.transform.localScale = new Vector3(2.0f, 0.05f, 2.0f);
        SetupMaterial(ring, color, 0.6f);

        // beam — thin tall cube
        var beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
        UnityEngine.Object.Destroy(beam.GetComponent<Collider>());
        beam.transform.SetParent(root.transform, false);
        beam.transform.localPosition = new Vector3(0f, 5f, 0f);
        beam.transform.localScale = new Vector3(0.12f, 10f, 0.12f);
        SetupMaterial(beam, color, 0.8f);

        // diamond — rotated cube on top
        var diamond = GameObject.CreatePrimitive(PrimitiveType.Cube);
        UnityEngine.Object.Destroy(diamond.GetComponent<Collider>());
        diamond.transform.SetParent(root.transform, false);
        diamond.transform.localPosition = new Vector3(0f, 11f, 0f);
        diamond.transform.localScale = new Vector3(0.7f, 0.7f, 0.7f);
        diamond.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
        SetupMaterial(diamond, color, 1.0f);

        _active.Add(new ActiveWaypoint
        {
            Root = root,
            Ring = ring,
            Beam = beam,
            Diamond = diamond,
            SpawnTime = Time.time,
            Color = color,
            BasePos = pos
        });

        PlayPing();
    }

    public void Update()
    {
        var cam = Camera.main;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var wp = _active[i];
            var age = Time.time - wp.SpawnTime;

            if (age > Lifetime)
            {
                DestroyWaypoint(wp);
                _active.RemoveAt(i);
                continue;
            }

            // distance-based scaling
            if (cam != null && wp.Root != null)
            {
                var dist = Vector3.Distance(cam.transform.position, wp.BasePos);
                var scale = BaseScale + dist * ScalePerUnit;
                wp.Root.transform.localScale = Vector3.one * scale;
            }

            // fade out
            if (age > FadeStart)
            {
                var alpha = 1f - (age - FadeStart) / (Lifetime - FadeStart);
                SetAlpha(wp.Ring, wp.Color, alpha * 0.6f);
                SetAlpha(wp.Beam, wp.Color, alpha * 0.8f);
                SetAlpha(wp.Diamond, wp.Color, alpha);
            }

            // spin the diamond
            if (wp.Diamond != null)
            {
                wp.Diamond.transform.Rotate(0f, 90f * Time.deltaTime, 0f, Space.World);
            }

            // gentle bob on the diamond
            if (wp.Diamond != null && wp.Root != null)
            {
                var bob = Mathf.Sin(Time.time * 2.5f) * 0.25f;
                wp.Diamond.transform.localPosition = new Vector3(0f, 11f + bob, 0f);
            }

            // pulse the ring scale slightly
            if (wp.Ring != null)
            {
                var pulse = 1f + Mathf.Sin(Time.time * 3f) * 0.08f;
                wp.Ring.transform.localScale = new Vector3(2.0f * pulse, 0.05f, 2.0f * pulse);
            }
        }
    }

    /// <summary>
    /// Draw screen-edge arrows for off-screen waypoints. Call from OnGUI.
    /// </summary>
    public void DrawOffScreenIndicators()
    {
        var cam = Camera.main;
        if (cam == null) return;

        var margin = 40f;

        foreach (var wp in _active)
        {
            if (wp.Root == null) continue;
            var age = Time.time - wp.SpawnTime;
            if (age > Lifetime) continue;

            var screenPos = cam.WorldToScreenPoint(wp.BasePos);

            // behind camera
            if (screenPos.z < 0)
            {
                screenPos.x = Screen.width - screenPos.x;
                screenPos.y = Screen.height - screenPos.y;
            }

            // flip Y for GUI coordinates (screen Y is bottom-up, GUI is top-down)
            var guiPos = new Vector2(screenPos.x, Screen.height - screenPos.y);

            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            bool onScreen = screenPos.z > 0
                && guiPos.x > margin && guiPos.x < Screen.width - margin
                && guiPos.y > margin && guiPos.y < Screen.height - margin;

            if (onScreen) continue;

            // find where the line from center to guiPos intersects the screen edge
            var dir = guiPos - center;
            var halfW = Screen.width * 0.5f - margin;
            var halfH = Screen.height * 0.5f - margin;

            // scale factor to reach each edge
            float t = float.MaxValue;
            if (Mathf.Abs(dir.x) > 0.001f)
                t = Mathf.Min(t, halfW / Mathf.Abs(dir.x));
            if (Mathf.Abs(dir.y) > 0.001f)
                t = Mathf.Min(t, halfH / Mathf.Abs(dir.y));

            var edgeX = center.x + dir.x * t;
            var edgeY = center.y + dir.y * t;

            // fade alpha with waypoint lifetime
            var alpha = age > FadeStart ? 1f - (age - FadeStart) / (Lifetime - FadeStart) : 1f;
            var color = wp.Color;
            color.a = alpha * 0.9f;

            DrawArrow(new Vector2(edgeX, edgeY), dir, color);
        }
    }

    public void Clear()
    {
        foreach (var wp in _active) DestroyWaypoint(wp);
        _active.Clear();
    }

    private void PlayPing()
    {
        try
        {
            // AudioSource and AudioClip are in UnityEngine.AudioModule which
            // can't be referenced at compile time (netstandard 2.1 vs 2.0),
            // so we drive everything through reflection.

            if (_pingClip == null)
            {
                var clipType = Type.GetType("UnityEngine.AudioClip, UnityEngine.AudioModule");
                if (clipType == null) return;

                const int sampleRate = 44100;
                const float duration = 0.15f;
                var sampleCount = (int)(sampleRate * duration);
                var data = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    var t = (float)i / sampleRate;
                    var envelope = 1f - t / duration;
                    data[i] = Mathf.Sin(2f * Mathf.PI * 880f * t) * envelope * 0.3f;
                }

                // AudioClip.Create(string name, int lengthSamples, int channels, int frequency, bool stream)
                var createMethod = clipType.GetMethod("Create", new[]
                    { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) });
                if (createMethod == null) return;

                _pingClip = createMethod.Invoke(null, new object[] { "WaypointPing", sampleCount, 1, sampleRate, false });
                if (_pingClip == null) return;

                // clip.SetData(float[], int)
                var setData = clipType.GetMethod("SetData", new[] { typeof(float[]), typeof(int) });
                setData?.Invoke(_pingClip, new object[] { data, 0 });
            }

            if (_audioSource == null)
            {
                var srcType = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule");
                if (srcType == null) return;

                _audioGo = new GameObject("WaypointAudio");
                UnityEngine.Object.DontDestroyOnLoad(_audioGo);
                _audioSource = _audioGo.AddComponent(srcType);

                // configure
                var playOnAwake = srcType.GetProperty("playOnAwake");
                playOnAwake?.SetValue(_audioSource, false);
                var spatialBlend = srcType.GetProperty("spatialBlend");
                spatialBlend?.SetValue(_audioSource, 0f);
                var volume = srcType.GetProperty("volume");
                volume?.SetValue(_audioSource, 0.5f);

                var clipType = _pingClip!.GetType();
                _playOneShotMethod = srcType.GetMethod("PlayOneShot", new[] { clipType });
            }

            _playOneShotMethod?.Invoke(_audioSource, new[] { _pingClip });
        }
        catch
        {
            // audio is nice-to-have, don't crash if it fails
        }
    }

    private static GUIStyle? _arrowStyle;

    private static void DrawArrow(Vector2 pos, Vector2 dir, UnityEngine.Color color)
    {
        var angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        if (angle < 0) angle += 360f;

        string arrow;
        if (angle < 22.5f || angle >= 337.5f) arrow = "\u25B6";      // right ▶
        else if (angle < 67.5f) arrow = "\u25E2";                     // down-right ◢
        else if (angle < 112.5f) arrow = "\u25BC";                    // down ▼
        else if (angle < 157.5f) arrow = "\u25E3";                    // down-left ◣
        else if (angle < 202.5f) arrow = "\u25C0";                    // left ◀
        else if (angle < 247.5f) arrow = "\u25E4";                    // up-left ◤
        else if (angle < 292.5f) arrow = "\u25B2";                    // up ▲
        else arrow = "\u25E5";                                         // up-right ◥

        if (_arrowStyle == null)
        {
            _arrowStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 72,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
        }

        _arrowStyle.normal.textColor = color;
        var size = 80f;
        GUI.Label(new Rect(pos.x - size * 0.5f, pos.y - size * 0.5f, size, size), arrow, _arrowStyle);
    }

    private static void SetupMaterial(GameObject go, Color color, float emissionStrength)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;
        var mat = r.material;

        mat.SetFloat("_Mode", 3);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;

        mat.color = color;
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", color * emissionStrength);
    }

    private static void SetAlpha(GameObject go, Color baseColor, float alpha)
    {
        if (go == null) return;
        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        var c = baseColor;
        c.a = alpha;
        r.material.color = c;

        var emissive = baseColor * 0.8f;
        emissive.a = alpha;
        r.material.SetColor("_EmissionColor", emissive);
    }

    private static void DestroyWaypoint(ActiveWaypoint wp)
    {
        if (wp.Root != null) UnityEngine.Object.Destroy(wp.Root);
    }

    private sealed class ActiveWaypoint
    {
        public GameObject Root = null!;
        public GameObject Ring = null!;
        public GameObject Beam = null!;
        public GameObject Diamond = null!;
        public float SpawnTime;
        public Color Color;
        public Vector3 BasePos;
    }
}
