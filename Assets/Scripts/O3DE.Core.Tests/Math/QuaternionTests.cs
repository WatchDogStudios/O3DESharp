namespace O3DE.Core.Tests.Math;

public class QuaternionTests
{
    [Theory]
    [InlineData(1f, 0f, 0f)]   // Right
    [InlineData(-1f, 0f, 0f)]  // Left
    [InlineData(0f, 0f, 1f)]   // Up
    [InlineData(0f, 0f, -1f)]  // Down
    [InlineData(1f, 1f, 0f)]   // diagonal, forward-right
    [InlineData(1f, 1f, 1f)]   // diagonal, forward-right-up
    [InlineData(-1f, 1f, 0.5f)] // arbitrary non-axis-aligned direction
    public void LookRotation_RotatesLocalForwardToTargetDirection(float x, float y, float z)
    {
        var dir = new Vector3(x, y, z).Normalized;

        var rotation = Quaternion.LookRotation(dir);
        var rotatedForward = rotation.RotatePoint(Vector3.Forward);

        rotatedForward.X.Should().BeApproximately(dir.X, 1e-4f);
        rotatedForward.Y.Should().BeApproximately(dir.Y, 1e-4f);
        rotatedForward.Z.Should().BeApproximately(dir.Z, 1e-4f);
    }

    [Fact]
    public void LookRotation_Forward_IsIdentity()
    {
        var rotation = Quaternion.LookRotation(Vector3.Forward);
        var rotatedForward = rotation.RotatePoint(Vector3.Forward);

        rotatedForward.X.Should().BeApproximately(Vector3.Forward.X, 1e-4f);
        rotatedForward.Y.Should().BeApproximately(Vector3.Forward.Y, 1e-4f);
        rotatedForward.Z.Should().BeApproximately(Vector3.Forward.Z, 1e-4f);
    }
}
