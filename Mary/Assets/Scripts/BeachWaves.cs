using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.VFX;

/// <summary>
/// A rolling wave that travels toward the shore on a loop, built on the HDRP "RollingWave" water sample.
/// Same idea as the sample's MoveWave script, but it moves relative to this object instead of world X.
///
/// Place this object at the shoreline and rotate it so its blue (Z) arrow points from the sea toward the land.
/// The Ocean's Deformation (and Foam) must be enabled.
/// </summary>
[ExecuteAlways]
public class BeachWaves : MonoBehaviour
{
    [Header("Wave")]
    [Tooltip("Created automatically. Needs a Water Decal material that deforms the water, e.g. WaveDeformer_SG from the Water Samples.")]
    public WaterDecal decal;
    [Tooltip("Optional: the sample's LipFoam visual effect. Driven by the same wave progress.")]
    public VisualEffect lipFoam;

    [Header("Motion")]
    [Tooltip("How far offshore the wave starts, in meters. It travels from here to this object's position.")]
    public float travelDistance = 100f;
    [Tooltip("Seconds for the wave to travel from the start to the shore.")]
    public float duration = 10f;
    [Tooltip("Seconds the wave keeps dissipating on the shore after it arrives: the swell fades while the foam lingers.")]
    public float linger = 6f;
    [Tooltip("Seconds to wait before the next wave.")]
    public float pause = 3f;
    [Tooltip("Wave size in the decal's region (along travel x along the shore), in meters.")]
    public Vector2 regionSize = new Vector2(40f, 150f);
    public float amplitude = 15f;

    [Header("Foam")]
    [Tooltip("Lip foam amount (vertical) over the wave's life: 0 = start of the run, 1 = arrival, then the linger phase (1 to 2).")]
    public AnimationCurve lipFoamOverLife = new AnimationCurve(
        new Keyframe(0f, 0f), new Keyframe(1f, 1f), new Keyframe(2f, 0.6f));
    [Range(0f, 1f)] public float surfaceFoam = 1f;
    [Range(0f, 1f)] public float deepFoam = 1f;
    [Tooltip("How much of the linger phase the foam holds at full strength before it fades.")]
    [Range(0f, 0.9f)] public float foamHold = 0.4f;

    [Header("Preview")]
    public bool autoplay = true;
    [Range(0f, 1f)] public float stateOverride = 0.5f;

    // The material property the sample's wave shader uses to animate (0 = start, 2 = finished).
    const string StateProperty = "_State";
    const string LipFactorProperty = "_factorWave";

    MaterialPropertyBlock block;
    float startTime;

    void Reset()
    {
        EnsureDecal();
#if UNITY_EDITOR
        if (decal != null && decal.material == null)
        {
            // WaveDeformer_SG from the HDRP Water Samples, if they have been imported.
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath("aab76e3e5058fdd4eb38e800f003d7d5");
            if (!string.IsNullOrEmpty(path))
                decal.material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
        }
#endif
    }

    void OnEnable()
    {
        EnsureDecal();
        startTime = CurrentTime();
    }

    void OnDisable()
    {
        if (decal != null) decal.gameObject.SetActive(false);
    }

    void EnsureDecal()
    {
        if (decal == null) decal = GetComponentInChildren<WaterDecal>(true);
        if (decal == null)
        {
            var child = new GameObject("Wave Decal");
            child.transform.SetParent(transform, false);
            decal = child.AddComponent<WaterDecal>();
        }

        decal.regionSize = regionSize;
        decal.amplitude = amplitude;
        decal.updateMode = CustomRenderTextureUpdateMode.Realtime;
        // The decal's X axis is its travel direction, so turn it to face this object's forward (toward land).
        decal.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
    }

    static float CurrentTime()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) return (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif
        return Time.time;
    }

    void Update()
    {
        if (decal == null) return;

        float runTime = Mathf.Max(0.1f, duration);
        float lingerTime = Mathf.Max(0f, linger);
        float cycle = runTime + lingerTime + Mathf.Max(0f, pause);

        float state;       // 0..1 travel progress
        float life;        // 0..2: travel, then 1..2 for the linger phase
        float swell = 1f;  // wave height scale
        float foam = 1f;   // foam scale
        bool active = true;
        if (autoplay)
        {
            float t = (CurrentTime() - startTime) % cycle;
            if (t < runTime)
            {
                state = t / runTime;
                life = state;
            }
            else
            {
                state = 1f;
                float k = lingerTime > 0f ? Mathf.Clamp01((t - runTime) / lingerTime) : 1f;
                active = t < runTime + lingerTime;
                life = 1f + k;
                swell = 1f - Mathf.SmoothStep(0f, 1f, k);                          // the swell dies quickly
                foam = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(foamHold, 1f, k)); // the foam hangs around
            }
        }
        else
        {
            state = Mathf.Clamp01(stateOverride);
            life = state;
        }

        decal.gameObject.SetActive(active);
        decal.regionSize = regionSize;
        decal.amplitude = amplitude * swell;
        decal.surfaceFoamDimmer = surfaceFoam * foam;
        decal.deepFoamDimmer = deepFoam * foam;

        // Start offshore and arrive at this object's position.
        decal.transform.localPosition = Vector3.back * (travelDistance * (1f - state));

        // Per-decal override, so the shared sample material asset is not modified.
        block ??= new MaterialPropertyBlock();
        block.SetFloat(StateProperty, state * 2f);
        decal.SetPropertyBlock(block);

        if (lipFoam != null)
            lipFoam.SetFloat(LipFactorProperty, lipFoamOverLife.Evaluate(life));
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Vector3 start = transform.position - transform.forward * travelDistance;
        Gizmos.DrawLine(start, transform.position);
        Gizmos.DrawWireSphere(transform.position, 1f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        // Wave footprint at the shore: width along the shore (X), thickness along travel (Z).
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(regionSize.y, 0.2f, regionSize.x));
    }
}
