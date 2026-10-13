using System.Collections.Generic;
using System.Text;
using Network;
using Unity.XR.PXR;
using UnityEngine;

namespace Robot
{
    public class ExtDevTracking : MonoBehaviour
    {
        private bool _enable = false;

        public void Start()
        {
            PXR_MotionTracking.ExtDevPassDataAction += ExtDevPassDataAction;
        }

        private void OnEnable()
        {
            // Enable expand-device custom data APIs (same capability gate as old pass-data state).
            PXR_MotionTracking.SetExtDevTrackerPassDataState(true);
        }

        private void OnDisable()
        {
            PXR_MotionTracking.SetExtDevTrackerPassDataState(false);
        }

        public void Update()
        {
            if (!_enable)
            {
                return;
            }

            // SDK 3.4.0: GetExtDevTrackerByPassData is obsolete(error).
            List<ExpandDevicesCustomData> dataArray;
            int result = PXR_MotionTracking.GetExpandDeviceCustomData(out dataArray);
            if (result != 0 || dataArray == null)
            {
                return;
            }

            for (int i = 0; i < dataArray.Count; i++)
            {
                PackageSend(dataArray[i]);
            }
        }

        [ContextMenu("Test")]
        public void Test()
        {
            byte[] snBytes = Encoding.ASCII.GetBytes("PA8E10MGH3229396D");
            string sn = "";
            for (var i = 0; i < snBytes.Length; i++)
            {
                sn += snBytes[i].ToString() + " ";
            }

            Debug.Log("sn:" + sn);
            byte[] data = { 0xA1, 0xB2, 0xC3, 0xD4 };

            byte[] package = PackageHandle.CustomPacket(CustomDataType.TRACKER_EXTRA_DEVICE, "PA8E10MGH3229396D",
                data);

            string msg = "";
            for (var i = 0; i < package.Length; i++)
            {
                msg += package[i].ToString() + " ";
            }

            Debug.Log("msg:" + msg);
            TcpHandler.SendCustomData(package);
        }

        private void PackageSend(ExpandDevicesCustomData passData)
        {
            byte[] payload = passData.data ?? System.Array.Empty<byte>();
            byte[] package = PackageHandle.CustomPacket(CustomDataType.TRACKER_EXTRA_DEVICE,
                passData.deviceId.ToString(), payload);
            TcpHandler.SendCustomData(package);
        }

        public void OnDestroy()
        {
            PXR_MotionTracking.ExtDevPassDataAction -= ExtDevPassDataAction;
            PXR_MotionTracking.SetExtDevTrackerPassDataState(false);
        }

        private void ExtDevPassDataAction(int value)
        {
            // 1 = start polling custom data; 0 = stop.
            _enable = value == 1;
        }
    }
}
