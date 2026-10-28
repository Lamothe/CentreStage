using CentreStage.Core;

namespace CentreStage.Core.Tests;

public class TriangleSortTests
{
    [Fact]
    public void Sort_FarTriangleFirst_RegardlessOfIndexOrder()
    {
        // Triangle 0 sits at depth -2 (far), triangle 1 at -0.5 (near). Index order
        // draws the near one first; the sort must flip them.
        float[] depth = { -2f, -2f, -2f, -0.5f, -0.5f, -0.5f };
        uint[] indices = { 3, 4, 5, 0, 1, 2 };
        var sorted = new uint[6];
        TriangleSort.SortBackToFront(depth, indices, sorted, new ulong[2]);

        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, sorted);
    }

    [Fact]
    public void Sort_TiesKeepOriginalTriangleOrder()
    {
        // Equal depths must not reorder: stability is what stops coplanar cards
        // flickering between frames.
        float[] depth = { -1f, -1f, -1f, -1f, -1f, -1f };
        uint[] indices = { 0, 1, 2, 3, 4, 5 };
        var sorted = new uint[6];
        TriangleSort.SortBackToFront(depth, indices, sorted, new ulong[2]);

        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, sorted);
    }

    [Fact]
    public void Sort_ReversesFrontToBackInput()
    {
        // Three triangles given near-to-far: output must be far-to-near.
        float[] depth = { -0.1f, -0.1f, -0.1f, -1f, -1f, -1f, -5f, -5f, -5f };
        uint[] indices = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var sorted = new uint[9];
        TriangleSort.SortBackToFront(depth, indices, sorted, new ulong[3]);

        Assert.Equal(new uint[] { 6, 7, 8, 3, 4, 5, 0, 1, 2 }, sorted);
    }

    [Fact]
    public void Sort_KeepsTriangleWinding()
    {
        // The three indices of a triangle travel together and keep their order.
        // Triangle (7,8,9) sums to -6, farther than (1,2,3) at -3: it draws first,
        // and its index order (7,8,9) survives the sort untouched.
        float[] depth = new float[10];
        Array.Fill(depth, -1f);
        depth[7] = depth[8] = depth[9] = -2f;
        uint[] indices = { 7, 8, 9, 1, 2, 3 };
        var sorted = new uint[6];
        TriangleSort.SortBackToFront(depth, indices, sorted, new ulong[2]);

        Assert.Equal(new uint[] { 7, 8, 9, 1, 2, 3 }, sorted);
    }

    [Fact]
    public void Sort_MixedDepthsOrderByCentroidSum()
    {
        // Triangles with mixed per-vertex depths order by their sum:
        // t0 = -3, t1 = -9 (farthest), t2 = -6.
        float[] depth = { -1f, -1f, -1f, -3f, -3f, -3f, -2f, -2f, -2f };
        uint[] indices = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var sorted = new uint[9];
        TriangleSort.SortBackToFront(depth, indices, sorted, new ulong[3]);

        Assert.Equal(new uint[] { 3, 4, 5, 6, 7, 8, 0, 1, 2 }, sorted);
    }

    [Fact]
    public void Sort_MixedSignDepthsStillOrderFarthestFirst()
    {
        // The camera can sit inside the geometry (orbit near-plane is tiny), so some
        // triangles legitimately have positive view depth. A signed key comparison
        // would drag them in front of everything — the regression the ulong fix guards.
        float[] depth = { 2f, 2f, 2f, -1f, -1f, -1f, -5f, -5f, -5f };
        uint[] indices = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var sorted = new uint[9];
        TriangleSort.SortBackToFront(depth, indices, sorted, new ulong[3]);

        Assert.Equal(new uint[] { 6, 7, 8, 3, 4, 5, 0, 1, 2 }, sorted);
    }

    [Fact]
    public void Sort_SingleTriangleIsUnchanged()
    {
        float[] depth = { -1f, -2f, -3f };
        uint[] indices = { 2, 0, 1 };
        var sorted = new uint[3];
        TriangleSort.SortBackToFront(depth, indices, sorted, new ulong[1]);

        Assert.Equal(new uint[] { 2, 0, 1 }, sorted);
    }
}
