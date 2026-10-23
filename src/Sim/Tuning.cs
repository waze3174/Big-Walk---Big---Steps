namespace BigSteps.Sim;

// tuning values and curves measured from baby steps.
// lengths are nate sized; NateSim.S scales them to the bird
public static class T
{
    public const float SimDt = 1f / 60f;          // baby steps' fixed step

    // body
    public const float LegLength = 0.7893356f;
    public const float BonusLegLength = 0.1f;
    public const float BonusLegLengthOneFoot = 0.14f;
    public const float HipHalfWidth = 0.124f;     // thigh_stretch.l/r local x
    public const float BodyWeight = 130.00002f;
    public const float LegWeight = 20.774f;
    public const float TorsoWeight = 88.45201f;
    public const float FootLength = 0.22f, FootWidth = 0.08f;
    public const float Grav = 9.8f;

    // lifted foot
    public const float SphereOffset = 0.5f;
    public const float MaxLegHeight = 0.3f;
    public const float MaxLegHeightBehind = 0.15f;
    public const float FootMinYToHip = 0.2f;
    public const float FootSpeed = 3f;
    public const float FootPlantSpeed = 3f;
    public const float MaxFootMovingDistFromHip = 1.25f;
    public const float FootDontCrossDist = 0.275f;
    public const float SoftSideStepConstraintAmount = 0.5f;
    public const float SideConstrainStickThresh = 50f;
    public const float LiftedSlipMult = 1f;

    // balance / hip target
    public const float BalanceHipWidth = 0.7f;
    public const float MaxTargetDist = 0.6f;
    public const float LeanCurveMaxTime = 0.45f;
    public const float CurStickTargetVel = 1.7f;  // min and max are both 1.7
    public const float StrafeLeanMult = 0.5f;
    public const float DontSquishLength = 0.6f;

    // legs
    public const float TwoFootLegP = 80f, TwoFootLegD = 0.8f;
    public const float OneFootLegP = 110f, OneFootLegD = 0.76f;
    public const float LegVStrength = 6000f;
    public const float LegTorqueP = 40f, LegTorqueD = 0.875f;
    public const float TorqueStickDotShit = 0.5f;
    public const float MaxFootTorque = 20f;
    public const float SlipQuadraticDrag = 0.025f;

    // lifting
    public const float PushOffWeightPercentThresh = 0.35f;
    public const float PushOffWeightThreshOppositeDir = 0.55f;
    public const float MaxTimeBeforeLift = 0.1f;
    public const float PushOffStrength = 0.1f;

    // hip yaw
    public const float MaxHipTwist = 50f;
    public const float FootHipYaw = 0.2f;
    public const float HipYawFootMagnitudeFactor = 1.1f;
    public const float HipYawP = 2f, HipYawD = 0.85f;

    // death
    public const float MagicFallOffTime = 0.3f;
    public const float DeadAngThresh = 30f;
    public const float InstaDeadAngThresh = 40f;
    public const float HipCollidingDeadAngThresh = 30f;
    public const float OneLegDeadAngThresh = 55f;
    public const float OffBalanceDeathTime = 0.125f;
    public const float DeathTime = 0.05f;
    public const float FootTooHighDistToHip = 0.1f;
    public const float HipCollisionDeathThresh = 10f;

    public static readonly Curve FootSpeedCurve = new(
        -0.00774765f, 1.0015259f, 0, 0,
        0.27460328f, 0.9775796f, -0.31974196f, -0.31974196f,
        0.7f, 0.05f, -0.123284854f, -0.123284854f,
        0.9333496f, 0.05f, 0, 0);

    public static readonly Curve LStickCurve = new(
        0.0040514814f, 0.029283646f, 0, 0,
        0.45130762f, 0.16194329f, 0.41899565f, 0.41899565f,
        0.7364517f, 0.44404417f, 1.230968f, 1.230968f,
        0.95153f, 0.99706334f, 1.8450766f, 1.8450766f,
        1, 1, 2, 2);

    public static readonly Curve LeanResponseCurve = new(
        0.0055024526f, 0.29624575f, 0.26675218f, 0.26675218f,
        1, 1, 0.81718785f, 0.81718785f);

    public static readonly Curve LegVStrengthExtendCurve = new(
        0.2614782f, 0.63501984f, 2.6451879f, 0.45698306f,
        0.95447487f, 0.7628389f, -0.3199166f, -2.4642603f,
        0.999f, 0.7f, 0, 0,
        1.0200853f, 0, 0, 0);

    public static readonly Curve MagicFallOff = new(
        0, 1, -0.00063963095f, -0.00063963095f,
        0.49910417f, 0.99968076f, -0.2328429f, -0.2328429f,
        1, 0.01f, -1.9758214f, -1.9758214f);

    public static readonly Curve TorqueFatigueCurve = new(
        -0.0023346292f, 0.99993086f, 0.05685023f, 0.05685023f,
        0.55594915f, 1.0042553f, -0.02827049f, -0.02827049f,
        0.78277105f, 0.5498521f, -1.8861763f, -1.8861763f,
        0.96962076f, 0.18919118f, -3.1500003f, -3.1500003f,
        0.99585676f, 0.05f, -0.5013092f, -0.5013092f);

    public static readonly Curve FatigueAddCurve = new(
        -0.022552967f, 0.3141445f, 0.16993813f, 0.16993813f,
        0.7015045f, 0.38382182f, -0.016687594f, -0.016687594f,
        0.99768066f, 0.7436218f, 0, 0);

    public static readonly Curve FootCrossPreventionCurve = new(
        0.0018692017f, 0.8487053f, 0, 0,
        0.9617376f, -0.15770167f, -1.8385612f, -0.0098911915f);
}

// baby steps' Surface.GetTractionCoef / GetQuadraticMult
public enum Surf { Rock, Dirt, Default, Mud, Gravel, Moss, Sand, SandCastle, Milk, Wood, Metal, Ice, Snow, Water, Grass }

public static class SurfTable
{
    public static float Traction(Surf s) => s switch
    {
        Surf.Rock => 0.88725f,
        Surf.Dirt => 0.9555f,
        Surf.Grass => 0.88f,          // grass on top caps traction at 0.88 (dirt/deadgrass 0.9555 under it)
        Surf.Mud => 0.5915f,
        Surf.Gravel or Surf.Moss => 0.728f,
        Surf.SandCastle => 0.7553f,
        Surf.Milk => 0.3185f,
        Surf.Wood => 1.092f,
        Surf.Ice => 0.1365f,
        _ => 0.91f,
    };

    public static float QuadMult(Surf s) => s switch
    {
        Surf.Rock or Surf.Metal => 0.8f,
        Surf.Mud or Surf.Moss or Surf.Milk => 0.75f,
        Surf.Snow or Surf.Sand or Surf.Gravel => 3f,
        Surf.SandCastle => 3.6f,
        Surf.Ice => 0.0025f,
        _ => 1f,
    };
}
