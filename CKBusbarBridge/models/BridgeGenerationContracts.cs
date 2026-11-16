using System;
using System.Collections.Generic;
using CKBusbarBridge.Models;
using CKBusbarBridge.Services;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeGenerationPlan
    {
        internal BridgeUpstreamSnapshot Upstream { get; set; }
        internal string UpstreamFingerprint { get; set; }
        internal string Fingerprint { get; set; }
        internal long Revision { get; set; }
        internal BridgeDesignSample Sample { get; set; }
        internal IReadOnlyList<BridgeDesignObject> Objects { get; set; }
        internal IReadOnlyList<AlgorithmBusbarGenerationGroup> Copper { get; set; }
        internal IReadOnlyList<FaceToFaceFramePositionPlan> Frames { get; set; }
        internal FaceToFaceFrameInsulatorParameters Insulators { get; set; }
        internal string Recipe { get; set; }
        internal double WallAppliedVerticalClearance { get; set; }
        internal CKBusbarBridge.Drivers.FaceToWallBaseVerticalCompensationPlan WallBaseCompensation { get; set; }
    }

    internal sealed class BridgeSecondaryDrivePlan
    {
        internal bool GenerateCopper { get; set; }
        internal IReadOnlyList<string> DirtyFrameSources { get; set; }
        internal IReadOnlyDictionary<string, string> FrameFingerprints { get; set; }
        internal string CopperFingerprint { get; set; }
    }

    internal enum BridgeGenerationOutcome { Unchanged, Completed, Restored, RecoveryRequired, SavedReadbackFailed, CopperRetained }

    internal sealed class BridgeGenerationResult
    {
        internal BridgeGenerationOutcome Outcome { get; }
        internal string Message { get; }
        internal BridgeDesignSample Readback { get; }
        internal BridgeGenerationResult(BridgeGenerationOutcome outcome, string message, BridgeDesignSample readback = null)
        { Outcome = outcome; Message = message; Readback = readback; }
    }

    // 每个操作对应生成阶段；fake不需要模仿SolidWorks COM。
    internal interface IBridgeModelGenerationSession
    {
        bool Preflight(BridgeGenerationPlan plan);
        void Begin();
        void GenerateCopper();
        void CheckpointCopper();
        void GenerateFrames();
        void Validate();
        void Commit();
        BridgeDesignSample Readback();
        void Complete();
        void Rollback();
        string RecoveryLocation { get; }
    }

    internal interface IBridgeBaseAngleGenerationStage
    {
        void DriveBaseBridgeAngles();
    }
}
