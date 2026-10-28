using System.Numerics;

namespace CentreStage.Core;

/// <summary>
/// Mutable scene pose. A GltfModel's world transforms are baked at load; playing an
/// animation means overriding some node locals every frame and walking the graph again.
/// This holds the working copy of the node transforms, recomputes world matrices
/// parent-first, and derives skin joint matrices from the result.
/// </summary>
public sealed class ScenePose
{
    private readonly int _nodeCount;
    private readonly int[] _parents;
    private readonly int[] _order;
    private readonly Matrix4x4[] _restLocal;
    private readonly Matrix4x4[] _worlds;
    private readonly bool[] _animated;

    // Rest/current TRS, only meaningful for nodes flagged _animated (the rest of the
    // graph keeps its exact rest matrix and never decomposes).
    private readonly Vector3[] _restT;
    private readonly Quaternion[] _restR;
    private readonly Vector3[] _restS;
    private readonly Vector3[] _curT;
    private readonly Quaternion[] _curR;
    private readonly Vector3[] _curS;
    private readonly bool[] _computed;

    // Last applied (clip, time), so a per-frame re-apply of an unchanged pose is a no-op.
    private RuntimeAnimation? _appliedClip;
    private float _appliedTime;
    private bool _poseSet;

    public ScenePose(GltfModel model)
    {
        var nodes = model.Root.Nodes ?? new List<Schema.GltfNode>();
        _nodeCount = nodes.Count;
        _parents = new int[_nodeCount];
        _order = new int[_nodeCount];
        _restLocal = new Matrix4x4[_nodeCount];
        _worlds = new Matrix4x4[_nodeCount];
        _animated = new bool[_nodeCount];
        _restT = new Vector3[_nodeCount];
        _restR = new Quaternion[_nodeCount];
        _restS = new Vector3[_nodeCount];
        _curT = new Vector3[_nodeCount];
        _curR = new Quaternion[_nodeCount];
        _curS = new Vector3[_nodeCount];
        _computed = new bool[_nodeCount];

        Array.Fill(_parents, -1);
        for (var i = 0; i < _nodeCount; i++)
        {
            foreach (var child in nodes[i].Children ?? new List<int>())
            {
                if (child >= 0 && child < _nodeCount)
                {
                    _parents[child] = i;
                }
            }

            _restLocal[i] = LocalOf(nodes[i]);
        }

        BuildOrder(nodes);

        // Nodes targeted by any channel of any clip switch to TRS composition so the
        // animated components can override them; everything else keeps its exact matrix.
        foreach (var anim in model.Animations)
        {
            foreach (var channel in anim.Channels)
            {
                if (channel.NodeIndex >= 0 && channel.NodeIndex < _nodeCount)
                {
                    _animated[channel.NodeIndex] = true;
                }
            }
        }

        for (var i = 0; i < _nodeCount; i++)
        {
            if (!_animated[i])
            {
                continue;
            }

            if (!Matrix4x4.Decompose(_restLocal[i], out _restS[i], out _restR[i], out _restT[i]))
            {
                // Matrix with shear or zero scale: fall back to the translation only.
                _restT[i] = _restLocal[i].Translation;
                _restR[i] = Quaternion.Identity;
                _restS[i] = Vector3.One;
            }

            _curT[i] = _restT[i];
            _curR[i] = _restR[i];
            _curS[i] = _restS[i];
        }

        Reset();
    }

    /// <summary>World transform of a manifest node index under the current pose.</summary>
    public Matrix4x4 World(int nodeIndex) => _worlds[nodeIndex];

    /// <summary>
    /// Increments whenever the pose actually changes. Renderers key per-frame work
    /// (translucent re-sorting) on this instead of redoing it every frame: a paused
    /// playback re-applies the same (clip, time) each frame and must not invalidate.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Applies one clip at <paramref name="time"/> (seconds, already wrapped by the
    /// caller) and recomputes worlds. Null restores the rest pose. Re-applying the
    /// same clip at the same time is a no-op.
    /// </summary>
    public void Apply(RuntimeAnimation? clip, float time)
    {
        if (clip is null)
        {
            Reset();
            return;
        }

        if (_poseSet && ReferenceEquals(clip, _appliedClip) && time == _appliedTime)
        {
            return;
        }

        for (var i = 0; i < _nodeCount; i++)
        {
            if (_animated[i])
            {
                _curT[i] = _restT[i];
                _curR[i] = _restR[i];
                _curS[i] = _restS[i];
            }
        }

        Span<float> value = stackalloc float[4];
        foreach (var channel in clip.Channels)
        {
            if (channel.Path == AnimationPath.Weights
                || channel.NodeIndex < 0
                || channel.NodeIndex >= _nodeCount
                || channel.SamplerIndex < 0
                || channel.SamplerIndex >= clip.Samplers.Count)
            {
                continue;
            }

            var sampler = clip.Samplers[channel.SamplerIndex];
            switch (channel.Path)
            {
                case AnimationPath.Translation:
                    if (AnimationEvaluation.TryEvaluate(sampler, time, 3, value))
                    {
                        _curT[channel.NodeIndex] = new Vector3(value[0], value[1], value[2]);
                    }

                    break;

                case AnimationPath.Scale:
                    if (AnimationEvaluation.TryEvaluate(sampler, time, 3, value))
                    {
                        _curS[channel.NodeIndex] = new Vector3(value[0], value[1], value[2]);
                    }

                    break;

                case AnimationPath.Rotation:
                    if (AnimationEvaluation.TryEvaluateRotation(sampler, time, value))
                    {
                        _curR[channel.NodeIndex] = new Quaternion(value[0], value[1], value[2], value[3]);
                    }

                    break;
            }
        }

        RecomputeWorlds();

        _appliedClip = clip;
        _appliedTime = time;
        _poseSet = true;
        Version++;
    }

    /// <summary>Restores the bind/rest pose.</summary>
    public void Reset()
    {
        if (_poseSet && _appliedClip is null)
        {
            return;
        }
        for (var i = 0; i < _nodeCount; i++)
        {
            if (_animated[i])
            {
                _curT[i] = _restT[i];
                _curR[i] = _restR[i];
                _curS[i] = _restS[i];
            }
        }

        RecomputeWorlds();

        _appliedClip = null;
        _appliedTime = 0f;
        _poseSet = true;
        Version++;
    }

    /// <summary>
    /// Skin joint matrices for the posed frame: inverseBind × jointWorld, one per joint.
    /// </summary>
    public void JointMatrices(RuntimeSkin skin, Span<Matrix4x4> destination)
    {
        for (var j = 0; j < skin.JointNodes.Count && j < destination.Length; j++)
        {
            var nodeIndex = skin.JointNodes[j];
            var world = nodeIndex >= 0 && nodeIndex < _nodeCount ? _worlds[nodeIndex] : Matrix4x4.Identity;
            destination[j] = skin.InverseBindMatrices[j] * world;
        }
    }

    private void RecomputeWorlds()
    {
        // Mirrors GltfModel.BuildScene's walk: a node whose parent is not yet resolved
        // (cycle, or multi-parent disagreement) is itself treated as a root, so a
        // malformed graph degrades the way the static bake degrades instead of
        // multiplying against an uncomputed (all-zero) parent world.
        Array.Clear(_computed);
        for (var i = 0; i < _nodeCount; i++)
        {
            var index = _order[i];
            var local = _animated[index]
                ? GltfMath.ComposeTrs(_curT[index], _curR[index], _curS[index])
                : _restLocal[index];
            var parent = _parents[index];
            _worlds[index] = parent >= 0 && _computed[parent]
                ? local * _worlds[parent]
                : local;
            _computed[index] = true;
        }
    }

    /// <summary>Parents before children; graph roots first, then any orphaned subgraphs.</summary>
    private void BuildOrder(List<Schema.GltfNode> nodes)
    {
        var visited = new bool[_nodeCount];
        var cursor = 0;

        void Walk(int index)
        {
            if (visited[index])
            {
                return;
            }

            visited[index] = true;
            _order[cursor++] = index;
            foreach (var child in nodes[index].Children ?? new List<int>())
            {
                if (child >= 0 && child < _nodeCount)
                {
                    Walk(child);
                }
            }
        }

        for (var i = 0; i < _nodeCount; i++)
        {
            if (_parents[i] < 0)
            {
                Walk(i);
            }
        }

        for (var i = 0; i < _nodeCount; i++)
        {
            // Orphaned or cyclic — treat as a graph root so it still gets a slot.
            Walk(i);
        }
    }

    private static Matrix4x4 LocalOf(Schema.GltfNode node) =>
        node.Matrix is { Length: 16 } m
            ? GltfMath.FromGltfMatrix(m)
            : GltfMath.ComposeTrs(
                ToVec3(node.Translation, Vector3.Zero),
                ToQuat(node.Rotation),
                ToVec3(node.Scale, Vector3.One));

    private static Vector3 ToVec3(float[]? v, Vector3 fallback) =>
        v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : fallback;

    private static Quaternion ToQuat(float[]? v) =>
        v is { Length: >= 4 } ? new Quaternion(v[0], v[1], v[2], v[3]) : Quaternion.Identity;
}
