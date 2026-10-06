using System.Collections.Generic;
using UnityEngine;

namespace SilhouetteOutline
{
    /// <summary>
    /// Registry of everything the outline pass draws: persistent instructions, per-frame instructions and
    /// <see cref="OutlineTarget"/> components.
    /// </summary>
    public static class OutlineManager
    {
        private static readonly Dictionary<int, OutlineInstruction> s_Persistent = new Dictionary<int, OutlineInstruction>();
        private static readonly List<OutlineInstruction> s_Frame = new List<OutlineInstruction>();
        private static readonly List<OutlineTarget> s_Targets = new List<OutlineTarget>();

        private static int s_NextHandle = 1;
        private static int s_FrameIndex = -1;

        // Static state survives play mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Persistent.Clear();
            s_Frame.Clear();
            s_Targets.Clear();
            s_NextHandle = 1;
            s_FrameIndex = -1;
        }

        /// <summary>Adds a persistent instruction. Returns a handle to pass to <see cref="RemoveInstruction"/>.</summary>
        public static int AddInstruction(OutlineInstruction instruction)
        {
            int handle = s_NextHandle++;
            s_Persistent[handle] = instruction;
            return handle;
        }

        /// <summary>Adds persistent instructions. Returns one handle per instruction.</summary>
        public static int[] AddInstructions(OutlineInstruction[] instructions)
        {
            var handles = new int[instructions.Length];
            for (int i = 0; i < instructions.Length; i++)
            {
                handles[i] = AddInstruction(instructions[i]);
            }

            return handles;
        }

        /// <summary>Replaces a persistent instruction, typically to move it. Returns false if the handle is unknown.</summary>
        public static bool UpdateInstruction(int handle, OutlineInstruction instruction)
        {
            if (!s_Persistent.ContainsKey(handle)) return false;

            s_Persistent[handle] = instruction;
            return true;
        }

        public static void RemoveInstruction(int handle)
        {
            s_Persistent.Remove(handle);
        }

        public static void RemoveInstructions(int[] handles)
        {
            if (handles == null) return;

            foreach (int handle in handles)
            {
                s_Persistent.Remove(handle);
            }
        }

        public static void ClearInstructions()
        {
            s_Persistent.Clear();
        }

        /// <summary>Adds an instruction drawn this frame only.</summary>
        public static void AddFrameInstruction(OutlineInstruction instruction)
        {
            FrameInstructions.Add(instruction);
        }

        /// <summary>Adds instructions drawn this frame only.</summary>
        public static void AddFrameInstructions(IEnumerable<OutlineInstruction> instructions)
        {
            FrameInstructions.AddRange(instructions);
        }

        internal static void Register(OutlineTarget target)
        {
            if (!s_Targets.Contains(target)) s_Targets.Add(target);
        }

        internal static void Unregister(OutlineTarget target)
        {
            s_Targets.Remove(target);
        }

        internal static bool HasWork => s_Persistent.Count > 0 || s_Targets.Count > 0 || FrameInstructions.Count > 0;

        internal static Dictionary<int, OutlineInstruction>.ValueCollection PersistentInstructions => s_Persistent.Values;

        internal static List<OutlineTarget> Targets => s_Targets;

        // Frame instructions live until the frame count changes, so every camera of the frame (both eyes included) sees them.
        internal static List<OutlineInstruction> FrameInstructions
        {
            get
            {
                if (s_FrameIndex != Time.frameCount)
                {
                    s_Frame.Clear();
                    s_FrameIndex = Time.frameCount;
                }

                return s_Frame;
            }
        }
    }
}
