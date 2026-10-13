using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>
    /// Where a portal side's physics lives when its container simulates in a physics frame (<c>docs/portals.md</c> D26, a planet that is a carrier): the
    /// frame's scene, the face in that scene's coordinates, and bodies' poses in them. A side that is not framed is its scope's own scene, as before.
    /// <para>
    /// A framed side's coordinates are the frame's <i>simulation</i> space (the frame-local position less the frame's floating origin) on a process that is
    /// simulating it, and its <i>render</i> space on a client that is only drawing it. The face is worked out in double from the link's absolute face and
    /// the frame's pose, and narrowed once, to the small frame-local float; every offset from it after that is small.
    /// </para>
    /// </summary>
    internal static class PortalSpaces
    {
        /// <summary>The physics frame's owner a container's content simulates in, or null when it simulates in its scope's own space.</summary>
        public static Container SpaceOf(Container container) => container != null ? container.InnerSpace : null;

        /// <summary>The scene a container's bodies simulate in: its frame's scene when it has a frame, its own scene otherwise. Invalid when the frame is not here (yet).</summary>
        public static Scene SceneOf(Container container)
        {
            if (container == null) return default;
            var space = container.InnerSpace;
            if (space == null) return container.gameObject.scene;
            var frame = space.Frame;
            return frame != null ? frame.Scene : default;
        }

        /// <summary>Whether a scene can hold physics objects now.</summary>
        public static bool Usable(Scene scene) => scene.IsValid() && scene.isLoaded;

        /// <summary>
        /// A face as a frame in the coordinates of <paramref name="space"/>'s scene, composed in double. For no space it is the scope's own scene, exactly
        /// as before (<see cref="ContainerRegistry.ToFrame(Double3, ulong)"/>). For a framed space: the absolute face goes into the scope's own coordinates
        /// in double, through the carrier's pose into the frame's local coordinates (<see cref="PhysicsFrames.ConvertLocal(Double3, Container, Container)"/>),
        /// and from there to the frame's simulation space, or its render space on a client that is not simulating it. Default (not valid) when the frame is not here.
        /// </summary>
        public static PortalFaceFrame FaceFrame(PortalFace face, Container space)
        {
            if (space == null) return new PortalFaceFrame(ContainerRegistry.ToFrame(face.Origin, face.Scope), face.Rotation);
            var frame = space.Frame;
            if (frame == null || !frame.Scene.IsValid()) return default;
            var scopePoint = ContainerRegistry.ToFramePrecise(face.Origin, face.Scope);
            var local = PhysicsFrames.ConvertLocal(scopePoint, null, space);
            var localRotation = Normalized(PhysicsFrames.ConvertLocal(face.Rotation, null, space));
            if (PhysicsFrames.InSimulationPose(space))
            {
                var offset = frame.RootOffset;
                var position = new Vector3((float)(local.X + offset.x), (float)(local.Y + offset.y), (float)(local.Z + offset.z));
                var rotation = frame.Root != null ? frame.Root.rotation * localRotation : localRotation;
                return new PortalFaceFrame(position, Normalized(rotation));
            }
            if (!frame._posed) return default; // a client's frame that has not been drawn yet has no pose to read
            return new PortalFaceFrame(frame.LocalToRenderPrecise(local).ToVector3(), Normalized(frame.LocalToRender(localRotation)));
        }

        /// <summary>
        /// A point of the coordinates entities of space <paramref name="from"/> are posed in, in those of <paramref name="to"/> (null: the scope's own). A space
        /// that is not being simulated here is in render space, where every position already is a scope position (<see cref="PhysicsFrames.ToScope"/>).
        /// Two spaces that are both simulated are converted directly, never through the scope's float.
        /// </summary>
        public static Vector3 Point(Vector3 point, Container from, Container to)
        {
            if (from == to) return point;
            bool fromSim = PhysicsFrames.InSimulationPose(from);
            bool toSim = PhysicsFrames.InSimulationPose(to);
            if (fromSim && toSim) return PhysicsFrames.Convert(point, from, to);
            if (fromSim) point = PhysicsFrames.Convert(point, from, null);
            if (toSim) point = PhysicsFrames.Convert(point, null, to);
            return point;
        }

        /// <summary><see cref="Point"/> for a rotation.</summary>
        public static Quaternion Rotation(Quaternion rotation, Container from, Container to)
        {
            if (from == to) return rotation;
            bool fromSim = PhysicsFrames.InSimulationPose(from);
            bool toSim = PhysicsFrames.InSimulationPose(to);
            if (fromSim && toSim) return PhysicsFrames.Convert(rotation, from, to);
            if (fromSim) rotation = PhysicsFrames.Convert(rotation, from, null);
            if (toSim) rotation = PhysicsFrames.Convert(rotation, null, to);
            return rotation;
        }

        private static Quaternion Normalized(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-6f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }
    }
}
