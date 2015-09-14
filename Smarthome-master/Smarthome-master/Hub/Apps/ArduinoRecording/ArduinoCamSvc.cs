using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ServiceModel;
using System.ServiceModel.Web;
using HomeOS.Hub.Platform.Views;

namespace HomeOS.Hub.Apps.ArduinoRecording
{

    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class ArduinoCamSvc : IArduinoCamContract
    {
        protected VLogger logger;
        ArduinoCam ArduinoCam;
        
        public ArduinoCamSvc(VLogger logger, ArduinoCam ArduinoCam)
        {
            this.logger = logger;
            this.ArduinoCam = ArduinoCam;
        }

        public string GetWebImage(string cameraFriendlyName)
        {
            try
            {
                byte[] image = ArduinoCam.GetImage(cameraFriendlyName);
                return Convert.ToBase64String(image, 0, image.Length, Base64FormattingOptions.InsertLineBreaks);
            }
            catch (Exception e)
            {
                logger.Log("Got exception in GetWebImage: " + e);
                return String.Empty;
            }
        }

        public List<string> ControlCamera(string control, string cameraFriendlyName)
        {
            try
            {
                string controlType = null;

                control = control.ToLower();

                switch (control)
                {
                    case "down":
                        controlType = Common.RolePTCamera.OpDownName;
                        break;
                    case "up":
                        controlType = Common.RolePTCamera.OpUpName;
                        break;
                    case "left":
                        controlType = Common.RolePTCamera.OpLeftName;
                        break;
                    case "right":
                        controlType = Common.RolePTCamera.OpRightName;
                        break;
                    case "zoomout":
                        controlType = Common.RolePTZCamera.OpZommOutName;
                        break;
                    case "zoomin":
                        controlType = Common.RolePTZCamera.OpZoomInName;
                        break;
                    default:
                        logger.Log("unknown camera command");
                        break;
                }

                if (controlType != null)
                    ArduinoCam.SendMsgToCamera(controlType, cameraFriendlyName);

                return new List<string>() { "" };
            }
            catch (Exception e)
            {
                logger.Log("Got exception in ControlCamera: " + e);
                return new List<string>() { e.Message };
            }
        }

        public List<string> GetRecordedClips(string cameraFriendlyName, int countMax)
        {
            try
            {

                string[] clips = ArduinoCam.GetRecordedClips(cameraFriendlyName, countMax);

                List<string> retList = new List<string>() { "" };

                retList.AddRange(clips);

                return retList;
            }
            catch (Exception ex)
            {
                logger.Log("Exception in GetRecordedClips({0}, {1}): {2}", cameraFriendlyName, countMax.ToString(), ex.ToString());
                return new List<string>() { "Exception: " + ex.Message };
            }
        }

        //returns [cameraName, roleName] pairs
        public List<string> GetCameraList()
        {
            try
            {
                List<string> retList = new List<string>() { "" };
                retList.AddRange(ArduinoCam.GetCameraList());
                return retList;
            }
            catch (Exception e)
            {
                logger.Log("Got exception in GetCameraList2: " + e);
                return new List<string>() { e.Message };
            }
        }

        public List<string> GetRecordedClipsCount(string cameraFriendlyName)
        {
            try 
            {
                int count = ArduinoCam.GetRecordedClipsCount(cameraFriendlyName);
                return new List<string>() { "", count.ToString() };
            }
            catch (Exception e)
            {
                logger.Log("Got exception in GetRecordedClipsCount for {0}: {1}", cameraFriendlyName, e.ToString());
                return new List<string>() {e.Message};
            }
        }

        public List<string> GetRecordedCamerasList()
        {
            try
            {
                string[] clips = ArduinoCam.GetRecordedCamerasList();

                List<string> retList = new List<string>() { "" };

                retList.AddRange(clips);

                return retList;
            }
            catch (Exception ex)
            {
                logger.Log("Exception in GetRecordedCamerasList: {0}", ex.ToString());
                return new List<string>() { "Exception: " + ex.Message };
            }
        }

        public void SendImage(string cameraFriendlyName)
        {
            try
            {
                logger.Log("start capturing");
                ArduinoCam.SendImage(cameraFriendlyName);
                return  ;
            }
            catch (Exception e)
            {
                logger.Log("Exception in sendImage for {0}: {1}", cameraFriendlyName, e.ToString());
                //return new List<string>() { "Got exception: " + e.Message };
            }
        }


        public List<string> StartOrContinueRecording(string cameraFriendlyName)
        {
            try
            {
                ArduinoCam.StartOrContinueRecording(cameraFriendlyName);                
                return new List<string>() { "" };
            }
            catch (Exception e)
            {
                logger.Log("Exception in startorcontinuerecording for {0}: {1}", cameraFriendlyName, e.ToString());
                return new List<string>() { "Got exception: " + e.Message };
            }
        }


        /// <summary>
        /// Called from UI to stop recording immediately
        /// </summary>
        /// <param name="cameraFriendlyName"></param>
        /// <returns></returns>
        public List<string> StopRecording(string cameraFriendlyName)
        {
            try
            {
                ArduinoCam.StopRecording(cameraFriendlyName);

                return new List<string>() { "" };
            }
            catch (Exception e)
            {
                logger.Log("Exception in startorcontinuerecording for {0}: {1}", cameraFriendlyName, e.ToString());
                return new List<string>() { "Got exception: " + e.Message };
            }
        }


        /// <summary>
        /// Sets (or turns off) Motion triggered recording for a camera
        /// </summary>
        /// <param name="cameraFriendlyName"></param>
        /// <param name="enable"></param>
        public List<string> EnableMotionTrigger(string cameraFriendlyName, bool enable)
        {
            try
            {

                ArduinoCam.EnableMotionTrigger(cameraFriendlyName, enable);
                return new List<string>() { "" };
            }
            catch (Exception e)
            {
                logger.Log("Exception in ArduinoCam:EnableMotionTrigger for {0}: {1}", cameraFriendlyName, e.ToString());
                return new List<string>() { "Got exception: " + e.Message };
            }
        }


        /// <summary>
        /// Returns whether motion triggered recording is enabled
        /// </summary>
        /// <param name="cameraFriendlyName"></param>
        /// <returns></returns>
        public List<string> IsMotionTriggerEnabled(string cameraFriendlyName)
        {
            try
            {
                bool result = ArduinoCam.IsMotionTriggerEnabled(cameraFriendlyName);
                List<string> retList = new List<string>() { "" , result.ToString() };
                return retList;

                
            }
            catch (Exception e)
            {
                logger.Log("Exception in ArduinoCam:IsMotionTriggerEnabled for {0}: {1}", cameraFriendlyName, e.ToString());
                return new List<string>() { "Got exception: " + e.Message };

            }
        }


        /// <summary>
        /// Sets (or turns off) Motion triggered recording for a camera
        /// </summary>
        /// <param name="cameraFriendlyName"></param>
        /// <param name="enable"></param>
        public List<string> EnableVideoUpload(string cameraFriendlyName, bool enable)
        {
            try
            {

                ArduinoCam.EnableVideoUpload(cameraFriendlyName, enable);
                return new List<string>() { "" };
            }
            catch (Exception e)
            {
                logger.Log("Exception in ArduinoCam:EnableMotionTrigger for {0}: {1}", cameraFriendlyName, e.ToString());
                return new List<string>() { "Got exception: " + e.Message };
            }
        }


        /// <summary>
        /// Returns whether motion triggered recording is enabled
        /// </summary>
        /// <param name="cameraFriendlyName"></param>
        /// <returns></returns>
        public List<string> IsVideoUploadEnabled(string cameraFriendlyName)
        {
            try
            {
                bool result = ArduinoCam.IsVideoUploadEnabled(cameraFriendlyName);
                List<string> retList = new List<string>() { "", result.ToString() };
                return retList;


            }
            catch (Exception e)
            {
                logger.Log("Exception in ArduinoCam:IsVideoUploadEnabled for {0}: {1}", cameraFriendlyName, e.ToString());
                return new List<string>() { "Got exception: " + e.Message };

            }
        }       

        public List<string> GetReceivedMessages()
        {
            List<string> retVal = new List<string>();
            try
            {
                retVal = ArduinoCam.GetReceivedMessages();
            }
            catch (Exception e)
            {
                logger.Log("Got exception in GetReceivedMessages : "+e);
            }
            return retVal;
        }

        public List<List<string>> GetDevicesList()
        {
            List<List<string>> retVal = new List<List<string>>();
            retVal = ArduinoCam.GetDeviceList();
            return retVal;
        }

    }


    [ServiceContract]
    public interface IArduinoCamContract
    {
        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.WrappedRequest, ResponseFormat = WebMessageFormat.Json)]
        List<List<string>> GetDevicesList();

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.WrappedRequest, ResponseFormat = WebMessageFormat.Json)]
        List<string> GetReceivedMessages();

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        void SendImage(string cameraFriendlyName);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.WrappedRequest, ResponseFormat = WebMessageFormat.Json)]
        string GetWebImage(string cameraFriendlyName);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.WrappedRequest, ResponseFormat = WebMessageFormat.Json)]
        List<string> ControlCamera(string control, string cameraFriendlyName);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        List<string> GetCameraList();

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        List<string> StartOrContinueRecording(string cameraFriendlyName);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        List<string> StopRecording(string cameraFriendlyName);

        //[OperationContract]
        //[WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        //void RecordVideo(string cameraFriendlyName, bool enable);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        List<string> EnableMotionTrigger(string cameraFriendlyName, bool enable);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        List<string> IsMotionTriggerEnabled(string cameraFriendlyName);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        List<string> EnableVideoUpload(string cameraFriendlyName, bool enable);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        List<string> IsVideoUploadEnabled(string cameraFriendlyName);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.Wrapped, ResponseFormat = WebMessageFormat.Json)]
        List<string> GetRecordedCamerasList();

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.WrappedRequest, ResponseFormat = WebMessageFormat.Json)]
        List<string> GetRecordedClipsCount(string cameraFriendlyName);

        [OperationContract]
        [WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.WrappedRequest, ResponseFormat = WebMessageFormat.Json)]
        List<string> GetRecordedClips(string cameraFriendlyName, int countMax);

        //[OperationContract]
        //[WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.WrappedRequest, ResponseFormat = WebMessageFormat.Json)]
        //int GetClipTriggerImagesCount(string clipUrl);

        //[OperationContract]
        //[WebInvoke(Method = "POST", BodyStyle = WebMessageBodyStyle.WrappedRequest, ResponseFormat = WebMessageFormat.Json)]
        //string[] GetClipTriggerImages(string clipUrl, int countMax);

        
    }          
}
