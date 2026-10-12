namespace Robot
{
    /// <summary>
    /// Idempotent camera-pipeline decisions for PXRCaptureState values
    /// 0 Idle, 1 Opening, 2 Opened, 3 Configured, 4 Photo, 5 VideoCapturing, 6 VideoPreviewing.
    /// Those numeric meanings come from capturelib PXRCamera.PXRCaptureState and must not be remapped.
    /// </summary>
    public static class CapturePipeline
    {
        public const int NativeCallSuccess = 1;
        public const int NativeOpenFailure = 0;

        public struct Flags
        {
            public bool Drive;
            public bool ConfigApplied;
            public bool PreviewApplied;
            public bool PreviewNotified;
            public bool PreviewEverStarted;
        }

        public struct Step
        {
            public bool SetConfig;
            public bool StartPreview;
            public bool NotifyRecordingReady;
            public bool DuplicateIgnored;
        }

        public static Step Advance(int state, ref Flags flags)
        {
            Step step = new Step();
            if (!flags.Drive)
            {
                return step;
            }

            switch (state)
            {
                case 0:
                    flags.ConfigApplied = false;
                    flags.PreviewApplied = false;
                    flags.PreviewNotified = false;
                    break;
                case 1:
                case 4:
                    break;
                case 2:
                    if (flags.ConfigApplied)
                    {
                        step.DuplicateIgnored = true;
                        break;
                    }

                    flags.ConfigApplied = true;
                    step.SetConfig = true;
                    break;
                case 3:
                    if (!flags.ConfigApplied)
                    {
                        flags.ConfigApplied = true;
                    }

                    if (flags.PreviewApplied)
                    {
                        step.DuplicateIgnored = true;
                        break;
                    }

                    flags.PreviewApplied = true;
                    flags.PreviewEverStarted = true;
                    step.StartPreview = true;
                    break;
                case 5:
                case 6:
                    if (!flags.PreviewApplied)
                    {
                        flags.ConfigApplied = true;
                        flags.PreviewApplied = true;
                        flags.PreviewEverStarted = true;
                    }

                    if (flags.PreviewNotified)
                    {
                        step.DuplicateIgnored = true;
                        break;
                    }

                    flags.PreviewNotified = true;
                    step.NotifyRecordingReady = true;
                    break;
            }

            return step;
        }

        public static string StateName(int state)
        {
            switch (state)
            {
                case 0: return "CAPTURE_STATE_IDLE";
                case 1: return "CAPTURE_STATE_CAMERA_OPENING";
                case 2: return "CAPTURE_STATE_CAMERA_OPENED";
                case 3: return "CAPTURE_STATE_CONFIGURED";
                case 4: return "CAPTURE_STATE_PHOTO_CAPTURING";
                case 5: return "CAPTURE_STATE_VIDEO_CAPTURING";
                case 6: return "CAPTURE_STATE_VIDEO_PREVIEWING";
                default: return "UNKNOWN_" + state;
            }
        }

        public static string SelfCheck()
        {
            Flags flags = new Flags { Drive = true };

            Step opened = Advance(2, ref flags);
            if (!opened.SetConfig || opened.StartPreview || opened.NotifyRecordingReady)
            {
                return "OPENED must call SetConfig once";
            }

            Step openedAgain = Advance(2, ref flags);
            if (!openedAgain.DuplicateIgnored || openedAgain.SetConfig)
            {
                return "duplicate OPENED must not call SetConfig again";
            }

            Step configured = Advance(3, ref flags);
            if (!configured.StartPreview || configured.SetConfig)
            {
                return "CONFIGURED must call StartPreview and not SetConfig";
            }

            Step configuredAgain = Advance(3, ref flags);
            if (!configuredAgain.DuplicateIgnored || configuredAgain.StartPreview)
            {
                return "duplicate CONFIGURED must not call StartPreview again";
            }

            Step preview = Advance(6, ref flags);
            if (!preview.NotifyRecordingReady || preview.SetConfig || preview.StartPreview)
            {
                return "VIDEO_PREVIEWING must notify recording once";
            }

            Step previewAgain = Advance(6, ref flags);
            if (!previewAgain.DuplicateIgnored || previewAgain.NotifyRecordingReady)
            {
                return "duplicate VIDEO_PREVIEWING must not notify again";
            }

            if (!flags.PreviewEverStarted)
            {
                return "preview must be remembered for cleanup";
            }

            Advance(0, ref flags);
            if (flags.ConfigApplied || flags.PreviewApplied || flags.PreviewNotified || !flags.PreviewEverStarted)
            {
                return "IDLE resets action flags and keeps PreviewEverStarted";
            }

            Flags skipped = new Flags { Drive = true };
            Step jumped = Advance(6, ref skipped);
            if (!jumped.NotifyRecordingReady || jumped.SetConfig || jumped.StartPreview || !skipped.ConfigApplied)
            {
                return "jump to VIDEO_PREVIEWING must not repeat SetConfig or StartPreview";
            }

            Flags stopped = new Flags { Drive = false, ConfigApplied = false };
            Step late = Advance(6, ref stopped);
            if (late.NotifyRecordingReady || late.SetConfig || late.StartPreview)
            {
                return "stopped pipeline must ignore a late preview callback";
            }

            Flags configuredOnly = new Flags { Drive = true };
            Step configFirst = Advance(3, ref configuredOnly);
            if (configFirst.SetConfig || !configFirst.StartPreview)
            {
                return "CONFIGURED without OPENED still needs StartPreview and not a second SetConfig";
            }

            return null;
        }
    }
}
