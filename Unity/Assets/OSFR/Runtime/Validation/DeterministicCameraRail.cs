using UnityEngine;

namespace OSFR.Validation
{
    /// <summary>
    /// Evaluates repeatable camera paths directly from a frame index. No transform
    /// integration or delta time is used, so a given frame always has the same pose.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class DeterministicCameraRail : MonoBehaviour
    {
        public enum RailPath
        {
            Forward,
            Lateral,
            MicroMotion
        }

        [SerializeField]
        private RailPath m_Path = RailPath.Forward;

        [SerializeField, Min(2)]
        private int m_FrameCount = 600;

        [SerializeField, Min(1.0f)]
        private float m_FrameRate = 60.0f;

        [SerializeField, Min(0)]
        private int m_FrameIndex;

        [SerializeField]
        private bool m_PlayAutomatically;

        [SerializeField]
        private bool m_Loop = true;

        public RailPath Path => m_Path;

        public int FrameCount => m_FrameCount;

        public float FrameRate => m_FrameRate;

        public int FrameIndex => m_FrameIndex;

        private void OnEnable()
        {
            ApplyFrame(m_FrameIndex);
        }

        private void LateUpdate()
        {
            ApplyFrame(m_FrameIndex);

            if (!Application.isPlaying || !m_PlayAutomatically)
            {
                return;
            }

            int nextFrame = m_FrameIndex + 1;
            m_FrameIndex = m_Loop
                ? nextFrame % m_FrameCount
                : Mathf.Min(nextFrame, m_FrameCount - 1);
        }

        public void Configure(RailPath path, int frameCount = 600, float frameRate = 60.0f)
        {
            m_Path = path;
            m_FrameCount = Mathf.Max(2, frameCount);
            m_FrameRate = Mathf.Max(1.0f, frameRate);
            m_FrameIndex = Mathf.Clamp(m_FrameIndex, 0, m_FrameCount - 1);
            ApplyFrame(m_FrameIndex);
        }

        public void ApplyFrame(int frameIndex)
        {
            m_FrameIndex = Mathf.Clamp(frameIndex, 0, m_FrameCount - 1);
            transform.SetPositionAndRotation(
                EvaluatePosition(m_FrameIndex),
                EvaluateRotation(m_FrameIndex));
        }

        public Vector3 EvaluatePosition(int frameIndex)
        {
            double normalizedTime = NormalizedTime(frameIndex);

            switch (m_Path)
            {
                case RailPath.Lateral:
                    return new Vector3((float)(-10.0 + 20.0 * normalizedTime), 1.6f, 25.0f);
                case RailPath.MicroMotion:
                    return new Vector3(0.0f, 1.6f, -10.0f);
                default:
                    return new Vector3(0.0f, 1.6f, (float)(-10.0 + 80.0 * normalizedTime));
            }
        }

        public Quaternion EvaluateRotation(int frameIndex)
        {
            if (m_Path == RailPath.Lateral)
            {
                Vector3 position = EvaluatePosition(frameIndex);
                return Quaternion.LookRotation(new Vector3(0.0f, 1.6f, 45.0f) - position, Vector3.up);
            }

            if (m_Path == RailPath.MicroMotion)
            {
                double seconds = Mathf.Clamp(frameIndex, 0, m_FrameCount - 1) / (double)m_FrameRate;
                float yaw = (float)System.Math.Sin(2.0 * System.Math.PI * seconds / 4.0);
                return Quaternion.Euler(0.0f, yaw, 0.0f);
            }

            return Quaternion.identity;
        }

        private double NormalizedTime(int frameIndex)
        {
            return Mathf.Clamp(frameIndex, 0, m_FrameCount - 1) / (double)(m_FrameCount - 1);
        }
    }
}
