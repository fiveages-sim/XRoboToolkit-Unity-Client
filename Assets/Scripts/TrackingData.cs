using System;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.XR;
using CommonUsages = UnityEngine.XR.CommonUsages;
using InputDevice = UnityEngine.XR.InputDevice;

namespace Robot
{
    public class TrackingData
    {
        private static volatile bool s_headOn;
        private static volatile bool s_controllerOn;
        private static volatile bool s_handTrackingOn;
        private static volatile TrackingType s_trackingTypeValue;

        public static bool HeadOn => s_headOn;
        public static bool ControllerOn => s_controllerOn;
        public static bool HandTrackingOn => s_handTrackingOn;
        public static TrackingType TrackingTypeValue => s_trackingTypeValue;

        private JsonData _motionTrackingJson = new JsonData();
        private JsonData _bodyTrackingJson = new JsonData();
        private JsonData _controllerDataJson = new JsonData();
        private JsonData _leftControllerJson = new JsonData();
        private JsonData _rightControllerJson = new JsonData();

        private JsonData _stateData = new JsonData();
        private JsonData _handData = new JsonData();
        private JsonData _leftHandData = new JsonData();
        private JsonData _rightHandData = new JsonData();

        public static void SetHeadOn(bool on)
        {
            s_headOn = on;
        }

        public static void SetControllerOn(bool on)
        {
            s_controllerOn = on;
        }

        public static void SetHandTrackingOn(bool on)
        {
            s_handTrackingOn = on;
        }

        public static void SetTrackingType(TrackingType trackingType)
        {
            s_trackingTypeValue = trackingType;
        }

        public static bool HasTracking
        {
            get
            {
                return (HeadOn || ControllerOn || HandTrackingOn ||
                        TrackingTypeValue > TrackingType.None);
            }
        }

        public void Get(ref JsonData totalData)
        {
            //sensor
            double predictTime = PXR_Enterprise.GetPredictedDisplayTime(); //毫秒
            predictTime = predictTime * 1000;
            totalData["predictTime"] = predictTime; //微秒，对应camera录制中帧插入的时间戳
            totalData["appState"] = _stateData;
            _stateData["focus"] = Application.isFocused;
            if (HeadOn)
            {
                if (TrackingDataSourceCtrl.UseEnterpriseSDK &&
                    EnterpriseCollectionRecorder.TryGetLatestEnterpriseHeadForTcp(out string enterprisePose, out int enterpriseStatus))
                {
                    JsonData enterpriseHeadJson = new JsonData();
                    enterpriseHeadJson["pose"] = enterprisePose;
                    enterpriseHeadJson["status"] = enterpriseStatus;
                    totalData["Head"] = enterpriseHeadJson;
                }
                else
                {
                    //  if (jsonData == null) jsonData = new JsonData();
                    //Right-handed coordinate system: X right, Y up, Z in
                    PxrSensorState2 sensor = new PxrSensorState2();
                    int sensorFrameIndex = 0;
                    PXR_System.GetPredictedMainSensorStateNew(ref sensor, ref sensorFrameIndex);
                    JsonData sensorJson = GetSensorJson(sensor);
                    //     sensorJson["handMode"] = (int)HandModeValue;
                    totalData["Head"] = sensorJson;
                    // SendToServerMessage("GetHeadTracking", sensorJson.ToJson());
                }
            }
            else
            {
                if (totalData.ContainsKey("Head"))
                    totalData.Remove("Head");
            }

            if (ControllerOn)
            {
                JsonData controller = GetControllerJsonDataBySource(predictTime);
                totalData["Controller"] = controller;
            }
            else
            {
                if (totalData.ContainsKey("Controller"))
                    totalData.Remove("Controller");
            }

            if (HandTrackingOn)
            {
                totalData["Hand"] = BuildHandJson();
            }
            else
            {
                if (totalData.ContainsKey("Hand"))
                    totalData.Remove("Hand");
            }

            if (TrackingTypeValue == TrackingType.Body && TryBuildBodyJson(out JsonData bodyJson))
            {
                totalData["Body"] = bodyJson;
            }
            else if (totalData.ContainsKey("Body"))
            {
                totalData.Remove("Body");
            }

            if (TrackingTypeValue == TrackingType.Motion && TryBuildMotionJson(out JsonData motionJson))
            {
                totalData["Motion"] = motionJson;
            }
            else if (totalData.ContainsKey("Motion"))
            {
                totalData.Remove("Motion");
            }


            long nsTime = Utils.GetCurrentTimestamp();
            totalData["timeStampNs"] = nsTime;
            ActiveInputDevice inputDevice = PXR_HandTracking.GetActiveInputDevice();
            totalData["Input"] = (int)inputDevice;
        }


        /// <summary>
        /// UnityXR schema for Hand. Same field layout for Enterprise sidechannel.
        /// </summary>
        public JsonData BuildHandJson()
        {
            HandJointLocations leftHand = new HandJointLocations();
            if (PXR_HandTracking.GetJointLocations(HandType.HandLeft, ref leftHand))
            {
                GetHandTrackingData(leftHand, ref _leftHandData);
                _handData["leftHand"] = _leftHandData;
            }

            HandJointLocations rightHand = new HandJointLocations();
            if (PXR_HandTracking.GetJointLocations(HandType.HandRight, ref rightHand))
            {
                GetHandTrackingData(rightHand, ref _rightHandData);
                _handData["rightHand"] = _rightHandData;
            }

            return _handData;
        }

        public enum HandSampleResult
        {
            Failed = 0,
            Unchanged = 1,
            Changed = 2
        }

        /// <summary>
        /// Per-side status from one GetJointLocations pair. Used for L/R isActive diag
        /// and true-rate estimation (raw vs quantized fingerprint, palm delta).
        /// </summary>
        public struct HandSampleInfo
        {
            public bool LeftOk;
            public bool RightOk;
            public uint LeftIsActive;
            public uint RightIsActive;
            /// <summary>OpenXR palm joint (index 0) when LeftOk and joints present.</summary>
            public bool LeftPalmValid;
            public Vector3 LeftPalm;
            public bool RightPalmValid;
            public Vector3 RightPalm;
            /// <summary>Full float-bit fingerprint (detects any SDK buffer change).</summary>
            public ulong RawFingerprint;
            /// <summary>
            /// Palm |Δxyz| vs previous sample (meters). -1 if no previous / no palm.
            /// Near 0 ⇒ repeat poll of same logical frame; larger ⇒ real motion or new frame.
            /// </summary>
            public float PalmDeltaM;
        }

        // Fingerprint quantization: ignore sub-threshold optical noise so Unchanged skips ToJson.
        // Position ~0.1mm; quat/scale/radius ~1e-4. Wire JSON still uses full ToString("R").
        private const float HandFpPosScale = 10000f;
        private const float HandFpUnitScale = 10000f;
        // Palm joint index (OpenXR / Pico HandJoint: Palm=0, Wrist=1).
        private const int HandPalmJointIndex = 0;

        /// <summary>
        /// Samples Hand for the aux sidechannel. False when neither hand updated this call.
        /// </summary>
        public bool TryBuildHandJson(out JsonData handJson)
        {
            return TrySampleHand(0, buildJsonEvenIfUnchanged: true, out handJson, out _, out _) !=
                   HandSampleResult.Failed;
        }

        /// <summary>
        /// Fingerprint quantized joint floats before ToString/ToJson. Unchanged skips ~5KB alloc.
        /// </summary>
        public HandSampleResult TrySampleHand(
            ulong previousFingerprint,
            bool buildJsonEvenIfUnchanged,
            out JsonData handJson,
            out ulong fingerprint)
        {
            return TrySampleHand(
                previousFingerprint,
                buildJsonEvenIfUnchanged,
                out handJson,
                out fingerprint,
                out _);
        }

        public HandSampleResult TrySampleHand(
            ulong previousFingerprint,
            bool buildJsonEvenIfUnchanged,
            out JsonData handJson,
            out ulong fingerprint,
            out HandSampleInfo info)
        {
            return TrySampleHand(
                previousFingerprint,
                default(Vector3),
                false,
                buildJsonEvenIfUnchanged,
                out handJson,
                out fingerprint,
                out info);
        }

        /// <param name="previousQuantFingerprint">Quantized fp used for publish gate.</param>
        /// <param name="previousPalm">Previous combined palm midpoint (or best available).</param>
        /// <param name="previousPalmValid">Whether previousPalm is usable for delta.</param>
        public HandSampleResult TrySampleHand(
            ulong previousQuantFingerprint,
            Vector3 previousPalm,
            bool previousPalmValid,
            bool buildJsonEvenIfUnchanged,
            out JsonData handJson,
            out ulong fingerprint,
            out HandSampleInfo info)
        {
            handJson = null;
            fingerprint = 0;
            info = default(HandSampleInfo);
            info.PalmDeltaM = -1f;
            ulong quantFp = 14695981039346656037UL; // FNV offset basis
            ulong rawFp = 14695981039346656037UL;
            HandJointLocations leftHand = new HandJointLocations();
            if (PXR_HandTracking.GetJointLocations(HandType.HandLeft, ref leftHand))
            {
                quantFp = MixHandFingerprint(quantFp, leftHand, quantize: true);
                rawFp = MixHandFingerprint(rawFp, leftHand, quantize: false);
                info.LeftOk = true;
                info.LeftIsActive = leftHand.isActive;
                if (TryGetPalm(leftHand, out Vector3 lp))
                {
                    info.LeftPalmValid = true;
                    info.LeftPalm = lp;
                }
            }

            HandJointLocations rightHand = new HandJointLocations();
            if (PXR_HandTracking.GetJointLocations(HandType.HandRight, ref rightHand))
            {
                quantFp = MixHandFingerprint(quantFp, rightHand, quantize: true);
                rawFp = MixHandFingerprint(rawFp, rightHand, quantize: false);
                info.RightOk = true;
                info.RightIsActive = rightHand.isActive;
                if (TryGetPalm(rightHand, out Vector3 rp))
                {
                    info.RightPalmValid = true;
                    info.RightPalm = rp;
                }
            }

            if (!info.LeftOk && !info.RightOk)
            {
                return HandSampleResult.Failed;
            }

            info.RawFingerprint = rawFp;
            fingerprint = quantFp;

            if (previousPalmValid)
            {
                Vector3 curPalm;
                bool curOk = false;
                if (info.LeftPalmValid && info.RightPalmValid)
                {
                    curPalm = 0.5f * (info.LeftPalm + info.RightPalm);
                    curOk = true;
                }
                else if (info.LeftPalmValid)
                {
                    curPalm = info.LeftPalm;
                    curOk = true;
                }
                else if (info.RightPalmValid)
                {
                    curPalm = info.RightPalm;
                    curOk = true;
                }
                else
                {
                    curPalm = default(Vector3);
                }

                if (curOk)
                {
                    info.PalmDeltaM = Vector3.Distance(curPalm, previousPalm);
                }
            }

            bool unchanged = previousQuantFingerprint != 0 && quantFp == previousQuantFingerprint;
            if (unchanged && !buildJsonEvenIfUnchanged)
            {
                return HandSampleResult.Unchanged;
            }

            if (info.LeftOk)
            {
                GetHandTrackingData(leftHand, ref _leftHandData);
                _handData["leftHand"] = _leftHandData;
            }

            if (info.RightOk)
            {
                GetHandTrackingData(rightHand, ref _rightHandData);
                _handData["rightHand"] = _rightHandData;
            }

            handJson = _handData;
            return unchanged ? HandSampleResult.Unchanged : HandSampleResult.Changed;
        }

        private static bool TryGetPalm(HandJointLocations hand, out Vector3 palm)
        {
            palm = default(Vector3);
            if (hand.jointLocations == null ||
                hand.jointCount <= HandPalmJointIndex ||
                HandPalmJointIndex >= hand.jointLocations.Length)
            {
                return false;
            }

            Posef pose = hand.jointLocations[HandPalmJointIndex].pose;
            palm = new Vector3(pose.Position.x, pose.Position.y, pose.Position.z);
            return true;
        }

        private static ulong MixHandFingerprint(ulong hash, HandJointLocations hand, bool quantize)
        {
            hash = MixU64(hash, hand.isActive);
            hash = MixU64(hash, hand.jointCount);
            hash = MixFloat(hash, hand.handScale, quantize, HandFpUnitScale);
            if (hand.jointLocations == null)
            {
                return hash;
            }

            int count = (int)Math.Min(hand.jointCount, (uint)hand.jointLocations.Length);
            for (int i = 0; i < count; i++)
            {
                HandJointLocation joint = hand.jointLocations[i];
                hash = MixU64(hash, (ulong)joint.locationStatus);
                hash = MixFloat(hash, joint.radius, quantize, HandFpUnitScale);
                hash = MixFloat(hash, joint.pose.Position.x, quantize, HandFpPosScale);
                hash = MixFloat(hash, joint.pose.Position.y, quantize, HandFpPosScale);
                hash = MixFloat(hash, joint.pose.Position.z, quantize, HandFpPosScale);
                hash = MixFloat(hash, joint.pose.Orientation.x, quantize, HandFpUnitScale);
                hash = MixFloat(hash, joint.pose.Orientation.y, quantize, HandFpUnitScale);
                hash = MixFloat(hash, joint.pose.Orientation.z, quantize, HandFpUnitScale);
                hash = MixFloat(hash, joint.pose.Orientation.w, quantize, HandFpUnitScale);
            }

            return hash;
        }

        private static ulong MixU64(ulong hash, ulong value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 1099511628211UL; // FNV prime
                return hash;
            }
        }

        private static ulong MixFloat(ulong hash, float value, bool quantize, float scale)
        {
            if (quantize)
            {
                int q = (int)Math.Round(value * scale);
                return MixU64(hash, unchecked((uint)q));
            }

            return MixU64(hash, unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        }

        public bool TryBuildBodyJson(out JsonData bodyJson)
        {
            bodyJson = null;
            bool isTracking = false;
            BodyTrackingStatus bodyStatus = new BodyTrackingStatus();
            int stateRet = PXR_MotionTracking.GetBodyTrackingState(ref isTracking, ref bodyStatus);
            if (stateRet != 0 || !isTracking || bodyStatus.stateCode != BodyTrackingStatusCode.BT_VALID)
            {
                return false;
            }

            BodyTrackingGetDataInfo dataInfo = new BodyTrackingGetDataInfo();
            BodyTrackingData trackingData = new BodyTrackingData();
            int ret = PXR_MotionTracking.GetBodyTrackingData(ref dataInfo, ref trackingData);
            if (ret != 0)
            {
                return false;
            }

            bodyJson = FillBodyTrackingJson(dataInfo, trackingData);
            return true;
        }

        public bool TryBuildMotionJson(out JsonData motionJson)
        {
            motionJson = FillMotionTrackingJson();
            return motionJson != null && motionJson.ContainsKey("len") && (int)motionJson["len"] > 0;
        }

        private JsonData FillMotionTrackingJson()
        {
            // SDK 3.4.0: GetMotionTrackerConnectStateWithSN / GetMotionTrackerLocations are obsolete(error).
            long[] trackerIds = MotionTrackerConnectionCache.GetConnectedIds();

            int len = 0;
            JsonData joints;
            if (_motionTrackingJson.ContainsKey("joints"))
            {
                joints = _motionTrackingJson["joints"];
            }
            else
            {
                joints = new JsonData();
                joints.SetJsonType(JsonType.Array);
                _motionTrackingJson["joints"] = joints;
            }

            for (int i = 0; i < trackerIds.Length; i++)
            {
                long trackerId = trackerIds[i];
                MotionTrackerLocation location = new MotionTrackerLocation();
                bool isValidPose = false;
                int result = PXR_MotionTracking.GetMotionTrackerLocation(trackerId, ref location, ref isValidPose);
                if (result != 0 || !isValidPose)
                {
                    continue;
                }

                JsonData joint;
                if (len < joints.Count)
                {
                    joint = joints[len];
                }
                else
                {
                    joint = new JsonData();
                    joints.Add(joint);
                }

                len++;
                joint["p"] = GetPoseStr(location.pose.Position, location.pose.Orientation);
                unsafe
                {
                    float* pVelo = location.linearVelocity;
                    float* pAcce = location.linearAcceleration;
                    float* pWVelo = location.angularVelocity;
                    float* pWAcce = location.angularAcceleration;

                    string va = pVelo[0] + "," + pVelo[1] + "," + pVelo[2] + "," + pWVelo[0] + "," +
                                pWVelo[1] + "," + pWVelo[2];
                    joint["va"] = va;
                    string wva = pAcce[0] + "," + pAcce[1] + "," + pAcce[2] + "," + pWAcce[0] + "," +
                                 pWAcce[1] + "," + pWAcce[2];
                    joint["wva"] = wva;
                }

                joint["sn"] = trackerId.ToString();
            }

            _motionTrackingJson["len"] = len;
            return _motionTrackingJson;
        }


        private JsonData FillBodyTrackingJson(BodyTrackingGetDataInfo dataInfo, BodyTrackingData trackingData)
        {
            JsonData joints;
            if (_bodyTrackingJson.ContainsKey("joints"))
            {
                joints = _bodyTrackingJson["joints"];
            }
            else
            {
                joints = new JsonData();
                joints.SetJsonType(JsonType.Array);
                _bodyTrackingJson["joints"] = joints;
            }

            if (trackingData.roleDatas != null)
            {
                int len = trackingData.roleDatas.Length;
                _bodyTrackingJson["len"] = len;

                for (int i = 0; i < len; i++)
                {
                    JsonData joint;
                    if (i < joints.Count)
                    {
                        joint = joints[i];
                    }
                    else
                    {
                        joint = new JsonData();
                        joints.Add(joint);
                    }

                    Vector3 pos = new Vector3((float)trackingData.roleDatas[i].localPose.PosX,
                        (float)trackingData.roleDatas[i].localPose.PosY,
                        -(float)trackingData.roleDatas[i].localPose.PosZ);

                    Quaternion rotation = new Quaternion((float)trackingData.roleDatas[i].localPose.RotQx,
                        (float)trackingData.roleDatas[i].localPose.RotQy,
                        -(float)trackingData.roleDatas[i].localPose.RotQz,
                        -(float)trackingData.roleDatas[i].localPose.RotQw);

                    joint["p"] = GetPoseStr(pos, rotation);

                    joint["t"] = trackingData.roleDatas[i].localPose.TimeStamp;
                    unsafe
                    {
                        fixed (double* pVelo = trackingData.roleDatas[i].velo, pAcce =
                                   trackingData.roleDatas[i].acce, pWVelo =
                                   trackingData.roleDatas[i].wvelo, pWAcce = trackingData.roleDatas[i].wacce)
                        {
                            string va = pVelo[0] + "," + pVelo[1] + "," + pVelo[2] + "," + pAcce[0] + "," + pAcce[1] +
                                        "," + pAcce[2];

                            joint["va"] = va;
                            string wva = pWVelo[0] + "," + pWVelo[1] + "," + pWVelo[2] + "," + pWAcce[0] + "," +
                                         pWAcce[1] +
                                         "," + pWAcce[2];

                            joint["wva"] = wva;
                        }
                    }
                }
            }

            return _bodyTrackingJson;
        }


        private JsonData GetLeftRightControllerJsonData(double predictTime)
        {
            Vector3 leftPosition =
                PXR_Input.GetControllerPredictPosition(PXR_Input.Controller.LeftController, predictTime);
            Quaternion leftRotation =
                PXR_Input.GetControllerPredictRotation(PXR_Input.Controller.LeftController, predictTime);


            InputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            RemoveEnterpriseControllerFields(_leftControllerJson);
            GetControllerJsonData(left, ref _leftControllerJson);
            _controllerDataJson["left"] = _leftControllerJson;
            _controllerDataJson["left"]["pose"] = GetPoseStr(leftPosition, leftRotation);

            Vector3 rightPosition =
                PXR_Input.GetControllerPredictPosition(PXR_Input.Controller.RightController, predictTime);
            Quaternion rightRotation =
                PXR_Input.GetControllerPredictRotation(PXR_Input.Controller.RightController, predictTime);

            InputDevice right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            RemoveEnterpriseControllerFields(_rightControllerJson);
            GetControllerJsonData(right, ref _rightControllerJson);
            _controllerDataJson["right"] = _rightControllerJson;
            _controllerDataJson["right"]["pose"] = GetPoseStr(rightPosition, rightRotation);

            return _controllerDataJson;
        }

        private JsonData GetControllerJsonDataBySource(double predictTime)
        {
            if (TrackingDataSourceCtrl.UseEnterpriseSDK)
            {
                EnterpriseCollectionRecorder.TryGetLatestEnterpriseControllerForTcp(
                    out EnterpriseCollectionRecorder.EnterpriseControllerTcpPose leftController,
                    out EnterpriseCollectionRecorder.EnterpriseControllerTcpPose rightController,
                    out _);
                if (PXR_HandTracking.GetActiveInputDevice() != ActiveInputDevice.ControllerActive)
                {
                    leftController = EnterpriseCollectionRecorder.CreateInvalidEnterpriseControllerTcpPose();
                    rightController = EnterpriseCollectionRecorder.CreateInvalidEnterpriseControllerTcpPose();
                }

                _controllerDataJson["left"] = GetEnterpriseControllerJsonData(leftController, ref _leftControllerJson);
                _controllerDataJson["right"] = GetEnterpriseControllerJsonData(rightController, ref _rightControllerJson);
                return _controllerDataJson;
            }

            return GetLeftRightControllerJsonData(predictTime);
        }

        private static JsonData GetEnterpriseControllerJsonData(
            EnterpriseCollectionRecorder.EnterpriseControllerTcpPose pose,
            ref JsonData json)
        {
            json["axisX"] = 0.0;
            json["axisY"] = 0.0;
            json["axisClick"] = false;
            json["grip"] = 0.0;
            json["trigger"] = 0.0;
            json["primaryButton"] = false;
            json["secondaryButton"] = false;
            json["menuButton"] = false;
            json["hasPose"] = pose.HasPose;
            json["pose"] = string.IsNullOrEmpty(pose.Pose)
                ? EnterpriseCollectionRecorder.InvalidControllerPose
                : pose.Pose;
            json["status"] = (double)pose.Status;
            json["timeStampNs"] = (double)pose.TimeStampNs;
            json["type"] = (double)pose.Type;
            json["poseError"] = (double)pose.PoseError;

            return json;
        }

        private static void RemoveEnterpriseControllerFields(JsonData json)
        {
            if (json.ContainsKey("hasPose"))
            {
                json.Remove("hasPose");
            }

            RemoveEnterprisePoseFields(json);
        }

        private static void RemoveEnterprisePoseFields(JsonData json)
        {
            if (json.ContainsKey("pose"))
            {
                json.Remove("pose");
            }

            if (json.ContainsKey("status"))
            {
                json.Remove("status");
            }

            if (json.ContainsKey("timeStampNs"))
            {
                json.Remove("timeStampNs");
            }

            if (json.ContainsKey("type"))
            {
                json.Remove("type");
            }

            if (json.ContainsKey("poseError"))
            {
                json.Remove("poseError");
            }
        }

        private static void GetControllerJsonData(InputDevice controllerDevice, ref JsonData json)
        {
            controllerDevice.TryGetFeatureValue(CommonUsages.primary2DAxis, out var axis2D);
            controllerDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out var axisClick);
            controllerDevice.TryGetFeatureValue(CommonUsages.grip, out var grip);
            controllerDevice.TryGetFeatureValue(CommonUsages.trigger, out var trigger);
            controllerDevice.TryGetFeatureValue(CommonUsages.primaryButton, out var primaryButton);
            controllerDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out var secondaryButton);
            controllerDevice.TryGetFeatureValue(CommonUsages.menuButton, out var menuButton);

            json["axisX"] = axis2D.x;
            json["axisY"] = axis2D.y;
            json["axisClick"] = axisClick;
            json["grip"] = grip;
            json["trigger"] = trigger;
            json["primaryButton"] = primaryButton;
            json["secondaryButton"] = secondaryButton;
            json["menuButton"] = menuButton;
        }


        private JsonData GetSensorJson(PxrSensorState2 sensorState)
        {
            JsonData jsonData = new JsonData();
            PxrPosef pose = sensorState.pose;

            Vector3 pos = new Vector3(pose.position.x, pose.position.y, pose.position.z);
            Quaternion rot = new Quaternion(pose.orientation.x, pose.orientation.y, pose.orientation.z,
                pose.orientation.w);
            jsonData["pose"] = GetPoseStr(pos, rot);
            jsonData["status"] = sensorState.status;

            //  jsonData["timeStampNs"] = sensorState.poseTimeStampNs;
            return jsonData;
        }


        private void GetHandTrackingData(HandJointLocations handJoint, ref JsonData json)
        {
            json["isActive"] = handJoint.isActive;
            json["count"] = handJoint.jointCount;
            json["scale"] = handJoint.handScale;

            JsonData jointLocationsJson;
            if (json.ContainsKey("HandJointLocations"))
            {
                jointLocationsJson = json["HandJointLocations"];
            }
            else
            {
                jointLocationsJson = new JsonData();
                jointLocationsJson.SetJsonType(JsonType.Array);
                json["HandJointLocations"] = jointLocationsJson;
            }

            for (int i = 0; i < handJoint.jointCount; i++)
            {
                JsonData jointJson;
                if (i < jointLocationsJson.Count)
                {
                    jointJson = jointLocationsJson[i];
                }
                else
                {
                    jointJson = new JsonData();
                    jointLocationsJson.Add(jointJson);
                }

                Posef posef = handJoint.jointLocations[i].pose;
                jointJson["p"] = GetPoseStr(posef.Position, posef.Orientation);
                jointJson["s"] = ((ulong)handJoint.jointLocations[i].locationStatus);
                jointJson["r"] = handJoint.jointLocations[i].radius;
            }
        }

        private string GetPoseStr(Vector3 position, Quaternion rotation)
        {
            return position.x.ToString("R") + "," + position.y.ToString("R") + "," + position.z.ToString("R") + "," +
                   rotation.x.ToString("R") + "," + rotation.y.ToString("R") + "," + rotation.z.ToString("R") + "," +
                   rotation.w.ToString("R");
        }

        private string GetPoseStr(Vector3f position, Quatf rotation)
        {
            return position.x.ToString("R") + "," + position.y.ToString("R") + "," + position.z.ToString("R") + "," +
                   rotation.x.ToString("R") + "," + rotation.y.ToString("R") + "," + rotation.z.ToString("R") + "," +
                   rotation.w.ToString("R");
        }

        public enum HandMode
        {
            Non = 0,
            Controller = 1,
            Hand = 2
        }


        public enum TrackingType
        {
            None = 0,
            Body = 1,
            Motion = 2
        }
    }
}
