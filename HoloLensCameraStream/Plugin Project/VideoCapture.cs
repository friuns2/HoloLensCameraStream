using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Media.Effects;
using Windows.Perception.Spatial;
using Windows.Foundation.Collections;
using Windows.Foundation;
using System.Diagnostics;
namespace HoloLensCameraStream
{
    public delegate void OnVideoCaptureResourceCreatedCallback(VideoCapture captureObject);
    public delegate void OnVideoModeStartedCallback(VideoCaptureResult result);
    public delegate void FrameSampleAcquiredCallback(VideoCaptureSample videoCaptureSample);
    public delegate void OnVideoModeStoppedCallback(VideoCaptureResult result);
    public sealed class VideoCapture
    {
        public static IEnumerable<Resolution> SupportedResolutions
        {
            get
            {
                throw new NotImplementedException("Please use the instance method VideoCapture.GetSupportedResolutions() for now.");
            }
        }
        public static IEnumerable<float> SupportedFrameRatesForResolution(Resolution resolution)
        {
            throw new NotImplementedException();
        }
        public event FrameSampleAcquiredCallback FrameSampleAcquired;
        public bool IsStreaming
        {
            get
            {
                return _frameReader != null;
            }
        }
        internal SpatialCoordinateSystem worldOrigin { get; private set; }
        public IntPtr WorldOriginPtr
        {
            set
            {
                worldOrigin = (SpatialCoordinateSystem)Marshal.GetObjectForIUnknown(value);
            }
        }
        static readonly MediaStreamType STREAM_TYPE = MediaStreamType.VideoPreview;
        static readonly Guid ROTATION_KEY = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");
        MediaFrameSourceGroup _frameSourceGroup;
        MediaFrameSourceInfo _frameSourceInfo;
        DeviceInformation _deviceInfo;
        MediaCapture _mediaCapture;
        MediaFrameReader _frameReader;
        VideoCapture(MediaFrameSourceGroup frameSourceGroup, MediaFrameSourceInfo frameSourceInfo, DeviceInformation deviceInfo)
        {
            _frameSourceGroup   = frameSourceGroup;
            _frameSourceInfo    = frameSourceInfo;
            _deviceInfo         = deviceInfo;
        }
        public static async void CreateAync(OnVideoCaptureResourceCreatedCallback onCreatedCallback)
        {
            var allFrameSourceGroups = await MediaFrameSourceGroup.FindAllAsync();                                              
            var candidateFrameSourceGroups = allFrameSourceGroups.Where(group => group.SourceInfos.Any(IsColorVideo));   
            var selectedFrameSourceGroup = candidateFrameSourceGroups.FirstOrDefault();                                         
            if (selectedFrameSourceGroup == null)
            {
                onCreatedCallback?.Invoke(null);
                return;
            }
            var selectedFrameSourceInfo = selectedFrameSourceGroup.SourceInfos.FirstOrDefault(); 
            if (selectedFrameSourceInfo == null)
            {
                onCreatedCallback?.Invoke(null);
                return;
            }
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);   
            var deviceInformation = devices.FirstOrDefault();                               
            if (deviceInformation == null)
            {
                onCreatedCallback(null);
                return;
            }
            var videoCapture = new VideoCapture(selectedFrameSourceGroup, selectedFrameSourceInfo, deviceInformation);
            await videoCapture.CreateMediaCaptureAsync();
            onCreatedCallback?.Invoke(videoCapture);
        }
        public IEnumerable<Resolution> GetSupportedResolutions()
        {
            List<Resolution> resolutions = new List<Resolution>();
            var allPropertySets = _mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(STREAM_TYPE).Select(x => x as VideoEncodingProperties); 
            foreach (var propertySet in allPropertySets)
            {
                resolutions.Add(new Resolution((int)propertySet.Width, (int)propertySet.Height));
            }
            return resolutions.AsReadOnly();
        }
        public IEnumerable<float> GetSupportedFrameRatesForResolution(Resolution resolution)
        {
            var allPropertySets = _mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(STREAM_TYPE).Select((x) => x as VideoEncodingProperties)
                .Where((x) =>
            {
                return x != null &&
                x.Width == (uint)resolution.width &&
                x.Height == (uint)resolution.height;
            }); 
            var frameRatesDict = new Dictionary<float, bool>();
            foreach (var propertySet in allPropertySets)
            {
                if (propertySet.FrameRate.Denominator != 0)
                {
                    float frameRate = (float)propertySet.FrameRate.Numerator / (float)propertySet.FrameRate.Denominator;
                    frameRatesDict.Add(frameRate, true);
                }
            }
            var frameRates = new List<float>();
            foreach (KeyValuePair<float, bool> kvp in frameRatesDict)
            {
                frameRates.Add(kvp.Key);
            }
            return frameRates.AsReadOnly();
        }
        public async void StartVideoModeAsync(CameraParameters setupParams, OnVideoModeStartedCallback onVideoModeStartedCallback)
        {
            var mediaFrameSource = _mediaCapture.FrameSources[_frameSourceInfo.Id]; 
            if (mediaFrameSource == null)
            {
                onVideoModeStartedCallback?.Invoke(new VideoCaptureResult(1, ResultType.UnknownError, false));
                return;
            }
            var pixelFormat = ConvertCapturePixelFormatToMediaEncodingSubtype(setupParams.pixelFormat);
            _frameReader = await _mediaCapture.CreateFrameReaderAsync(mediaFrameSource, pixelFormat);
            _frameReader.FrameArrived += HandleFrameArrived;
            await _frameReader.StartAsync();
            VideoEncodingProperties properties = GetVideoEncodingPropertiesForCameraParams(setupParams);
            if (setupParams.rotateImage180Degrees)
            {
                properties.Properties.Add(ROTATION_KEY, 180);
            }
			IVideoEffectDefinition ved = new VideoMRCSettings( setupParams.enableHolograms, setupParams.enableVideoStabilization, setupParams.videoStabilizationBufferSize, setupParams.hologramOpacity );
			await _mediaCapture.AddVideoEffectAsync(ved, MediaStreamType.VideoPreview);
            await _mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(STREAM_TYPE, properties);
            onVideoModeStartedCallback?.Invoke(new VideoCaptureResult(0, ResultType.Success, true));
        }
        public void RequestNextFrameSample(FrameSampleAcquiredCallback onFrameSampleAcquired)
        {
            if (onFrameSampleAcquired == null)
            {
                throw new ArgumentNullException("onFrameSampleAcquired");
            }
            if (IsStreaming == false)
            {
                throw new Exception("You cannot request a frame sample until the video mode is started.");
            }
            TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> handler = null;
            handler = (MediaFrameReader sender, MediaFrameArrivedEventArgs args) =>
            {
                using (var frameReference = _frameReader.TryAcquireLatestFrame()) 
                {
                    if (frameReference != null)
                    {
                        onFrameSampleAcquired.Invoke(new VideoCaptureSample(frameReference, worldOrigin));
                    }
                    else
                    {
                        onFrameSampleAcquired.Invoke(null);
                    }
                }
                _frameReader.FrameArrived -= handler;
            };
            _frameReader.FrameArrived += handler;
        }
        public async void StopVideoModeAsync(OnVideoModeStoppedCallback onVideoModeStoppedCallback)
        {
            if (IsStreaming == false)
            {
                onVideoModeStoppedCallback?.Invoke(new VideoCaptureResult(1, ResultType.InappropriateState, false));
                return;
            }
            _frameReader.FrameArrived -= HandleFrameArrived;
            await _frameReader.StopAsync();
            _frameReader.Dispose();
            _frameReader = null;
            onVideoModeStoppedCallback?.Invoke(new VideoCaptureResult(0, ResultType.Success, true));
        }
        public void Dispose()
        {
            if (IsStreaming)
            {
                throw new Exception("Please make sure StopVideoModeAsync() is called before displosing the VideoCapture object.");
            }
            _mediaCapture?.Dispose();
        }
        async Task CreateMediaCaptureAsync()
        {
            if (_mediaCapture != null)
            {
                throw new Exception("The MediaCapture object has already been created.");
            }
            _mediaCapture = new MediaCapture();
            await _mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings()
            {
                VideoDeviceId = _deviceInfo.Id,
                SourceGroup = _frameSourceGroup,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu, 
                StreamingCaptureMode = StreamingCaptureMode.Video
            });
            _mediaCapture.VideoDeviceController.Focus.TrySetAuto(true);
        }
        void HandleFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (FrameSampleAcquired == null)
            {
                return;
            }
            using (var frameReference = _frameReader.TryAcquireLatestFrame()) 
            {
                if (frameReference != null)
                {
                    var sample = new VideoCaptureSample(frameReference, worldOrigin);
                    FrameSampleAcquired?.Invoke(sample);
                }
            }
        }
        VideoEncodingProperties GetVideoEncodingPropertiesForCameraParams(CameraParameters cameraParams)
        {
            var allPropertySets = _mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(STREAM_TYPE).Select((x) => x as VideoEncodingProperties)
                .Where((x) =>
            {
                if (x == null) return false;
                if (x.FrameRate.Denominator == 0) return false;
                double calculatedFrameRate = (double)x.FrameRate.Numerator / (double)x.FrameRate.Denominator;
                return
                x.Width == (uint)cameraParams.cameraResolutionWidth &&
                x.Height == (uint)cameraParams.cameraResolutionHeight &&
                (int)Math.Round(calculatedFrameRate) == cameraParams.frameRate;
            }); 
            if (allPropertySets.Count() == 0)
            {
                throw new Exception("Could not find an encoding property set that matches the given camera parameters.");
            }
            var chosenPropertySet = allPropertySets.FirstOrDefault();
            return chosenPropertySet;
        }
        static bool IsColorVideo(MediaFrameSourceInfo sourceInfo)
        {
            return (sourceInfo.MediaStreamType == STREAM_TYPE &&
                sourceInfo.SourceKind == MediaFrameSourceKind.Color);
        }
        static string ConvertCapturePixelFormatToMediaEncodingSubtype(CapturePixelFormat format)
        {
            switch (format)
            {
                case CapturePixelFormat.BGRA32:
                    return MediaEncodingSubtypes.Bgra8;
                case CapturePixelFormat.NV12:
                    return MediaEncodingSubtypes.Nv12;
                case CapturePixelFormat.JPEG:
                    return MediaEncodingSubtypes.Jpeg;
                case CapturePixelFormat.PNG:
                    return MediaEncodingSubtypes.Png;
                default:
                    return MediaEncodingSubtypes.Bgra8;
            }
        }
    }
	public class VideoMRCSettings : IVideoEffectDefinition
    {
        public string ActivatableClassId
        {
            get
            {
                return "Windows.Media.MixedRealityCapture.MixedRealityCaptureVideoEffect";
            }
        }
        public IPropertySet Properties
        {
            get; private set;
        }
        public VideoMRCSettings(bool HologramCompositionEnabled, bool VideoStabilizationEnabled, int VideoStabilizationBufferLength, float GlobalOpacityCoefficient)
        {
            Properties = (IPropertySet)new PropertySet();
            Properties.Add("HologramCompositionEnabled", HologramCompositionEnabled);
            Properties.Add("VideoStabilizationEnabled", VideoStabilizationEnabled);
            Properties.Add("VideoStabilizationBufferLength", VideoStabilizationBufferLength);
            Properties.Add("GlobalOpacityCoefficient", GlobalOpacityCoefficient);
        }
    }
}
