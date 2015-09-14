using System;
using System.ComponentModel;
using System.Net;
using System.Collections.Generic;
using System.Threading;
using System.AddIn;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.ServiceModel;
using System.Net.Mail;
using System.Windows;
using System.Windows.Media.Imaging;
using MySql.Data.MySqlClient;
using System.Timers;
using System.Net.Mail;

using HomeOS.Hub.Common;
using HomeOS.Hub.Platform.Views;
using SmartRecorder;
using HomeOS.Hub.Common.Bolt.DataStore;

using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;

namespace HomeOS.Hub.Apps.ArduinoRecording
{
    public enum SwitchType { Binary, Multi };

    class SwitchInfo
    {
        public VCapability Capability { get; set; }
        public double Level { get; set; }
        public SwitchType Type { get; set; }

        public bool IsColored { get; set; }
        public Color Color { get; set; }
    }

    enum MediaType
    {
        MediaType_Video_MP4,
        MediaType_Image_JPEG
    };

    class CameraInfo
    {
        public VCapability Capability { get; set; }
        public byte[] LastImageBytes { get; set; }
        public Bitmap BitmapImage { get; set; }
        public VideoWriter VideoWriter { get; set; }
        public ObjectDetector ObjectDetector { get; set; }
        public bool ObjectFound { get; set; }
        public Rectangle LastObjectRect { get; set; }
        public BackgroundWorker BackgroundWorkerObjectDetector { get; set; }
        public DateTime CurrVideoStartTime { get; set; }
        public DateTime CurrVideoEndTime { get; set; }
        public bool RecordVideo { get; set; }
        public bool EnableObjectTrigger { get; set; }
        public bool UploadVideo { get; set; }
    }

    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    [ServiceKnownType(typeof(ArduinoCam.CameraControl))]
    [AddIn("HomeOS.Hub.Apps.ArduinoRecording")]
    public class ArduinoCam : ModuleBase
    {
        private static TimeSpan OneSecond = new TimeSpan(0, 0, 1);
        DateTime lastSet = DateTime.Now - OneSecond;

        Dictionary<VPort, SwitchInfo> registeredSwitches = new Dictionary<VPort, SwitchInfo>();
        Dictionary<string, VPort> switchFriendlyNames = new Dictionary<string, VPort>();

        //For Speech
        List<VPort> speechPorts = new List<VPort>();
        //for doing the disco thing speech command is "GO DISCO" (if you have speech reco role installed)
        private static System.Timers.Timer discoTimer;
        private static int countDiscoEvents = 0;
        private const int maxDiscoEvents = 5;
        private Color[] colorChoices = new Color[] { Color.Red, Color.Pink, Color.Blue, Color.Purple, Color.Orange };

        /// <summary>
        /// 
        /// </summary>
        const int VIDEO_FPS_NUM = 24;
        const int VIDEO_FPS_DEN = 1;
        const int VIDEO_ENC_FRAMERATE = 240000;
        static TimeSpan DEFAULT_VIDEO_CLIP_LEN = new TimeSpan(0, 0, 10); //30 seconds

        const string VIDEO_SUB_DIR_NAME = "videos";

        public enum CameraControl { Left, Right, Up, Down, ZoomIn, ZoomOut };

        //list of accessible dummy ports in the system
        List<VPort> accessibleDummyPorts;

        private SafeServiceHost serviceHost;

        private Dictionary<VPort, CameraInfo> registeredCameras = new Dictionary<VPort, CameraInfo>();
        private Dictionary<string, VPort> cameraFriendlyNames = new Dictionary<string, VPort>();


        private WebFileServer appServer;
        private WebFileServer recordingServer;

        List<string> receivedMessageList;

        string videosDir;
        string videosBaseUrl;
        string uploadfilename;

        SafeThread worker = null;
        SafeThread ardui_worker = null;

        IStream datastream;

        int push_event_check = 0;
        private int timerCount = 0;
        private int recordingController = 0;

        public override void Start()
        {
            logger.Log("Started: {0}", ToString());

            ArduinoCamSvc service = new ArduinoCamSvc(logger, this);
            serviceHost = new SafeServiceHost(logger, typeof(IArduinoCamContract), service, this, Constants.AjaxSuffix, moduleInfo.BaseURL());
            serviceHost.Open();

            appServer = new WebFileServer(moduleInfo.BinaryDir(), moduleInfo.BaseURL(), logger);

            this.videosDir = moduleInfo.WorkingDir() + "\\" + VIDEO_SUB_DIR_NAME;
            this.videosBaseUrl = moduleInfo.BaseURL() + "/" + VIDEO_SUB_DIR_NAME;

            recordingServer = new WebFileServer(videosDir, videosBaseUrl, logger);

            logger.Log("camera service is open for business at " + moduleInfo.BaseURL());

            //........... instantiate the list of other ports that we are interested in
            accessibleDummyPorts = new List<VPort>();

            //..... get the list of current ports from the platform
            IList<VPort> allPortsList = GetAllPortsFromPlatform();

            if (allPortsList != null)
            {
                foreach (VPort port in allPortsList)
                {
                    PortRegistered(port);
                }
            }

            this.receivedMessageList = new List<string>();

            // initiate a data stream to upload images or video files to a Server
            // remoteSync flag can be set to true, if the Platform Settings has the Cloud storage
            // information i.e., DataStoreAccountName, DataStoreAccountKey values
            datastream = base.CreateFileDataStream<StrKey, StrValue>("test", true /* remoteSync */);

            //ardui_worker = new SafeThread(delegate()
            //{
            //    SerialWork();
            //}, "AppArduinoCam-worker", logger);
            //ardui_worker.Start();
        }

        public override void Stop()
        {
            lock (this)
            {
                if (worker != null)
                    worker.Abort();

                if (datastream != null)
                    datastream.Close();

                if (serviceHost != null)
                    serviceHost.Close();

                //close all windows
                foreach (VPort cameraPort in registeredCameras.Keys)
                {
                    StopRecording(cameraPort, true /* force */);
                }

                if (appServer != null)
                    appServer.Dispose();

                if (recordingServer != null)
                    recordingServer.Dispose();
            }
        }

        /// <summary>
        /// Sit in a loop and spray the Pings to all active ports
        /// </summary>
        public void SerialWork()
        {
            int counter = 0;
            while (true)
            {
                counter++;

                lock (this)
                {
                    foreach (VPort port in accessibleDummyPorts)
                    {
                        SendEchoRequest(port, counter);
                    }
                }

                //WriteToStream();
                System.Threading.Thread.Sleep(1 * 10 * 1000);
            }
        }

        public void SendEchoRequest(VPort port, int counter)
        {
            try
            {
                DateTime requestTime = DateTime.Now;

                var retVals = Invoke(port, RoleDummy.Instance, RoleDummy.OpEchoName, new ParamType(counter));

                double diffMs = (DateTime.Now - requestTime).TotalMilliseconds;

                if (retVals[0].Maintype() != (int)ParamType.SimpleType.error)
                {

                    int rcvdNum = (int)retVals[0].Value();

                    logger.Log("echo success to {0} after {1} ms. sent = {2} rcvd = {3}", port.ToString(), diffMs.ToString(), counter.ToString(), rcvdNum.ToString());
                }
                else
                {
                    logger.Log("echo failure to {0} after {1} ms. sent = {2} error = {3}", port.ToString(), diffMs.ToString(), counter.ToString(), retVals[0].Value().ToString());
                }

            }
            catch (Exception e)
            {
                logger.Log("Error while calling echo request: {0}", e.ToString());
            }
        }




        /// <summary>
        ///  Called when a new port is registered with the platform
        /// </summary>
        /// <param name="port"></param>
        public override void PortRegistered(VPort port)
        {
            lock (this)
            {
                if (Role.ContainsRole(port, RoleCamera.RoleName))
                {
                    if (!registeredCameras.ContainsKey(port))
                    {
                        InitCamera(port);
                    }
                    else
                    {
                        //the friendly name of the port might have changed. update that.
                        string oldFriendlyName = null;

                        foreach (var pair in cameraFriendlyNames)
                        {
                            if (pair.Value.Equals(port) &&
                                !pair.Key.Equals(port.GetInfo().GetFriendlyName()))
                            {
                                oldFriendlyName = pair.Key;
                                break;
                            }
                        }

                        if (oldFriendlyName != null)
                        {
                            cameraFriendlyNames.Remove(oldFriendlyName);
                            cameraFriendlyNames.Add(port.GetInfo().GetFriendlyName(), port);
                        }
                    }

                }
                else if (!accessibleDummyPorts.Contains(port) &&
                    Role.ContainsRole(port, RoleDummy.RoleName) &&
                    GetCapabilityFromPlatform(port) != null)
                {
                    accessibleDummyPorts.Add(port);

                    logger.Log("{0} added port {1}", this.ToString(), port.ToString());

                    if (Subscribe(port, RoleDummy.Instance, RoleDummy.OpEchoSubName))
                        logger.Log("{0} subscribed to port {1}", this.ToString(), port.ToString());
                    else
                        logger.Log("failed to subscribe to port {1}", this.ToString(), port.ToString());
                }
                else if (Role.ContainsRole(port, RoleSwitchMultiLevel.RoleName) ||
                    Role.ContainsRole(port, RoleSwitchBinary.RoleName) ||
                    Role.ContainsRole(port, RoleLightColor.RoleName))
                {
                    if (!registeredSwitches.ContainsKey(port) &&
                        GetCapabilityFromPlatform(port) != null)
                    {
                        var switchType = (Role.ContainsRole(port, RoleSwitchMultiLevel.RoleName)) ? SwitchType.Multi : SwitchType.Binary;

                        bool colored = Role.ContainsRole(port, RoleLightColor.RoleName);

                        InitSwitch(port, switchType, colored);
                    }

                }

                else if (Role.ContainsRole(port, RoleSpeechReco.RoleName))
                {

                    if (!speechPorts.Contains(port) &&
                        GetCapabilityFromPlatform(port) != null)
                    {

                        speechPorts.Add(port);

                        logger.Log("SwitchController:{0} added speech port {1}", this.ToString(), port.ToString());


                        //TODO Call it with phrases we care about - FOR NOW HARD CODED in Kinect driver
                        //  var retVal = Invoke(port, RoleSpeechReco.Instance, RoleSpeechReco.OpSetSpeechPhraseName, new ParamType(ParamType.SimpleType.text, "on"));

                        //subscribe to speech reco
                        if (Subscribe(port, RoleSpeechReco.Instance, RoleSpeechReco.OpPhraseRecognizedSubName))
                            logger.Log("{0} subscribed to port {1}", this.ToString(), port.ToString());
                    }
                }
            }
        }

        /// <summary>
        ///  Called when a new port is deregistered with the platform
        /// </summary>
        /// <param name="port"></param>
        public override void PortDeregistered(VPort port)
        {
            lock (this)
            {
                if (Role.ContainsRole(port, RoleCamera.RoleName))
                {
                    if (registeredCameras.ContainsKey(port))
                    {
                        ForgetCamera(port);
                        logger.Log("{0} deregistered camera port {1}", this.ToString(), port.GetInfo().ModuleFacingName());
                    }
                }
                else if (accessibleDummyPorts.Contains(port))
                {
                    accessibleDummyPorts.Remove(port);
                    logger.Log("{0} deregistered port {1}", this.ToString(), port.GetInfo().ModuleFacingName());
                }
                else if (registeredSwitches.ContainsKey(port))
                {
                    ForgetSwitch(port);
                }

            }
        }

        public void SendImage(string cameraFriendlyName)
        {
            logger.Log("Start getting image");
            worker = new SafeThread(delegate()
            {
                Work(cameraFriendlyName);
            }, "ArduinoCamSvc", logger);
            worker.Start();
        }

        /// <summary>
        /// Sit in a loop and spray the Pings to all active ports
        /// </summary>
        public void Work(string cameraFriendlyName)
        {
            //byte[] image = GetImage(cameraFriendlyName);
            //string imageData = Convert.ToBase64String(image, 0, image.Length, Base64FormattingOptions.InsertLineBreaks);
            //WriteToStream(imageData);

            //...capture image and save it in local directory. Then, upload to the Azure Blob using Azure API
            byte[] image = GetImage(cameraFriendlyName);
            string filepath = GetMediaFileName(cameraFriendlyName, MediaType.MediaType_Image_JPEG);
            var fs = new BinaryWriter(new FileStream(filepath, FileMode.Append, FileAccess.Write));
            fs.Write(image);
            fs.Close();
            System.Threading.Thread.Sleep(1 * 3 * 1000);
            logger.Log("Succesfully saved file to local HomeHub.");
            //UploadToAzure(filepath);
            System.Threading.Thread.Sleep(1 * 10 * 1000);
        }

        public void UploadToAzure(string filepath)
        {
            string accountName = GetConfSetting("DataStoreAccountName");
            string accountKey = GetConfSetting("DataStoreAccountKey");
            try
            {

                StorageCredentials creds = new StorageCredentials(accountName, accountKey);
                CloudStorageAccount account = new CloudStorageAccount(creds, useHttps: true);

                CloudBlobClient client = account.CreateCloudBlobClient();

                CloudBlobContainer imagesBlobContainer = client.GetContainerReference("lotstore");
                if (imagesBlobContainer.CreateIfNotExists())
                {
                    // Enable public access on the newly created "images" container.
                    imagesBlobContainer.SetPermissions(
                        new BlobContainerPermissions
                        {
                            PublicAccess = BlobContainerPublicAccessType.Blob
                        });
                }

                string blobfilename = Path.GetFileName(filepath);
                logger.Log("blobfilename: {0}.", blobfilename);
                CloudBlockBlob blob = imagesBlobContainer.GetBlockBlobReference(blobfilename);

                using (Stream file = System.IO.File.OpenRead(filepath))
                {
                    blob.UploadFromStream(file);
                    logger.Log("Succesfully uploaded file to Azure.");
                }

            }
            catch (Exception ex)
            {
                logger.Log("UploadToAzure threw an exception!");
            }
        }

        public void WriteToStream(string imageData) //
        {
            StrKey key = new StrKey("ArduinoCamKey");
            datastream.Append(key, new StrValue(imageData));
            logger.Log("Writing imagedata to stream ");
        }

        public byte[] GetImage(string friendlyName)
        {
            lock (this)
            {
                if (cameraFriendlyNames.ContainsKey(friendlyName))
                    return registeredCameras[cameraFriendlyNames[friendlyName]].LastImageBytes;

                throw new Exception("Unknown camera " + friendlyName);
            }
        }

        System.Timers.Timer timer_motion = null;
        System.Timers.Timer timer_stoprecord = null;
        System.Timers.Timer timer_bluetooth = null;
        Thread smartHome = null;

        Thread myThread = null;
        public override void OnNotification(string roleName, string opName, IList<VParamType> retVals, VPort senderPort)
        {
            timerCount++;
            if (timerCount >= 50)
            {
                timerCount = 0;
                //push_event_check = 0;

                /*
                if (push_event_check == 1)
                {
                    push_event_check = 2; timerCount = 0;
                    recordingController = 2;
                    Console.WriteLine(this.ToString() + " : Stopped Recording");
                }*/
            }

            if (registeredCameras.ContainsKey(senderPort))
            {
                if (retVals.Count >= 1 && retVals[0].Value() != null)
                {
                    byte[] imageBytes = (byte[])retVals[0].Value();

                    lock (this)
                    {
                        if (recordingController == 1)
                        {
                            registeredCameras[senderPort].RecordVideo = true;
                            recordingController = 0;
                        }
                        else if (recordingController == 2)
                        {
                            StopRecording(senderPort, true);
                            recordingController = 0;
                        }

                        registeredCameras[senderPort].LastImageBytes = imageBytes;

                        if (registeredCameras[senderPort].RecordVideo ||
                            registeredCameras[senderPort].EnableObjectTrigger)
                        {
                            bool addFrame = false;
                            Rectangle rectObject = new Rectangle(0, 0, 0, 0);
                            MemoryStream stream = new MemoryStream(imageBytes);
                            Bitmap image = null;
                            image = (Bitmap)Image.FromStream(stream);
                            if (null != registeredCameras[senderPort].BitmapImage)
                            {
                                registeredCameras[senderPort].BitmapImage.Dispose();
                                registeredCameras[senderPort].BitmapImage = null;
                            }
                            registeredCameras[senderPort].BitmapImage = image;

                            //lets check if the image is what we expect
                            if (image.PixelFormat != PixelFormat.Format24bppRgb)
                            {
                                string message = String.Format("Image  format from {0} is not correct. PixelFormat: {1}",
                                                                senderPort.GetInfo().GetFriendlyName(), image.PixelFormat);
                                logger.Log(message);

                                return;
                            }

                            // stop if needed
                            StopRecording(senderPort, false /* force*/);

                            //// if recording is underway don't bother that, it will stop after that clip time lapses
                            //// if recording needs to be done only on motion (object) triggers, check with the result of the object
                            //// detector above
                            //if (registeredCameras[senderPort].RecordVideo)
                            //{
                            //    //if record video is still true, see if we need to add his frame
                            //    if (registeredCameras[senderPort].VideoWriter != null || !registeredCameras[senderPort].EnableObjectTrigger)
                            //    {
                            //        addFrame = true;
                            //    }
                            //    else
                            //    {
                            //        if (registeredCameras[senderPort].ObjectFound)
                            //            addFrame = true;
                            //    }
                            //}

                            if (registeredCameras[senderPort].RecordVideo)
                            {
                                addFrame = true;
                            }
                            else
                            {
                                if (registeredCameras[senderPort].EnableObjectTrigger &&
                                    registeredCameras[senderPort].ObjectFound)
                                    addFrame = true;
                            }

                            if (addFrame)
                            {

                                StartRecording(senderPort, image.Width, image.Height, VIDEO_FPS_NUM, VIDEO_FPS_DEN, VIDEO_ENC_FRAMERATE);

                                long sampleTime = (DateTime.Now - registeredCameras[senderPort].CurrVideoStartTime).Ticks;

                                AddFrameToVideo(image, senderPort, sampleTime);

                                if (registeredCameras[senderPort].ObjectFound)
                                {
                                    registeredCameras[senderPort].ObjectFound = false;
                                    rectObject = registeredCameras[senderPort].LastObjectRect;
                                    WriteObjectImage(senderPort, image, rectObject, true /* center */);
                                }

                            }
                        }
                    }
                }
                else
                {
                    logger.Log("{0} got null image", this.ToString());
                }
            }
            else if (accessibleDummyPorts.Contains(senderPort))
            {

                string message;
                lock (this)
                {
                    switch (opName.ToLower())
                    {
                        case RoleDummy.OpEchoSubName:
                            int rcvdNum = (int)retVals[0].Value();
                            if (rcvdNum == 1)
                            {
                                message = "Bluetooth Signal";
                                if (timer_motion != null)
                                {
                                    timer_motion.Stop();
                                    timer_stoprecord.Stop();
                                }
                                if (timer_bluetooth != null)
                                {
                                    timer_bluetooth.Stop();
                                    timer_bluetooth.Start();
                                }
                                if (push_event_check == 1)
                                {
                                    push_event_check = 2;
                                    timerCount = 0;
                                    recordingController = 2;
                                    Console.WriteLine(this.ToString() + " : Stopped Recording");
                                }
                            }
                            else if (rcvdNum == 2)
                            {
                                message = "Motion Signal";
                                if (myThread == null)
                                {
                                    myThread = new Thread(new ThreadStart(SendMail));
                                    myThread.Start();
                                }

                                if (smartHome == null)
                                {
                                    smartHome = new Thread(new ThreadStart(SmartHomeRun));
                                    smartHome.Start();
                                }
                                if (timer_bluetooth == null)
                                {
                                    timer_bluetooth = new System.Timers.Timer();
                                    timer_bluetooth.Interval = 10 * 1000;
                                    timer_bluetooth.Elapsed += new System.Timers.ElapsedEventHandler(timer_bluetooth_Elapsed);
                                    timer_bluetooth.Start();
                                }

                                if (timer_motion == null)
                                {
                                    timer_motion = new System.Timers.Timer();
                                    timer_motion.Interval = 5 * 1000;
                                    timer_motion.Elapsed += new System.Timers.ElapsedEventHandler(timer_motion_Elapsed);
                                    timer_motion.Start();

                                    timer_stoprecord = new System.Timers.Timer();
                                    timer_stoprecord.Interval = 60 * 1000;
                                    timer_stoprecord.Elapsed += new System.Timers.ElapsedEventHandler(timer_Elapsed);
                                    timer_stoprecord.Start();
                                }
                                else
                                {
                                    timer_motion.Start();
                                    timer_stoprecord.Start();
                                }
                            }
                            else if (rcvdNum == 3)
                            {
                                message = "Bluetooth & Motion Siganl";
                                if (smartHome == null)
                                {
                                    smartHome = new Thread(new ThreadStart(SmartHomeRun));
                                    smartHome.Start();
                                }
                                if (timer_bluetooth == null)
                                {
                                    timer_bluetooth = new System.Timers.Timer();
                                    timer_bluetooth.Interval = 10 * 1000;
                                    timer_bluetooth.Elapsed += new System.Timers.ElapsedEventHandler(timer_bluetooth_Elapsed);
                                    timer_bluetooth.Start();
                                }
                                if (timer_bluetooth != null)
                                {
                                    timer_bluetooth.Stop();
                                    timer_bluetooth.Start();
                                }
                                if (timer_motion != null)
                                {
                                    timer_motion.Stop();
                                    timer_stoprecord.Stop();
                                }
                                if (push_event_check == 1)
                                {
                                    push_event_check = 2; timerCount = 0;
                                    recordingController = 2;
                                    Console.WriteLine(this.ToString() + " : stopped Recording");
                                }
                            }
                            else if (rcvdNum == 0)
                            {
                                message = "Waitting";
                                smartHome = null;
                                myThread = null;
                            }
                            else
                            {
                                message = "error";
                            }
                            this.receivedMessageList.Add(message);
                            Console.WriteLine(message);
                            break;
                        default:
                            message = String.Format("Invalid async operation return {0} from {1}", opName.ToLower(), senderPort.ToString());
                            logger.Log(message);
                            break;
                    }
                }
                logger.Log("{0} {1}", this.ToString(), message);
            }

            lock (this)
            {
                //check if notification is speech event
                if (roleName.Contains(RoleSpeechReco.RoleName) && opName.Equals(RoleSpeechReco.OpPhraseRecognizedSubName))
                {
                    string rcvdCmd = (string)retVals[0].Value();

                    switch (rcvdCmd)
                    {
                        case "ALLON":
                            SetAllSwitches(1.0);
                            break;

                        case "ALLOFF":
                            SetAllSwitches(0.0);
                            break;

                        case "PLAYMOVIE":
                            SetAllSwitches(0.1);
                            break;

                        case "DISCO":
                            DiscoSwitches();
                            break;

                    }
                    return;
                }

                switch (opName)
                {
                    case RoleSwitchBinary.OpGetName:
                        {
                            if (retVals.Count >= 1 && retVals[0].Value() != null)
                            {
                                bool level = (bool)retVals[0].Value();

                                registeredSwitches[senderPort].Level = (level) ? 1 : 0;
                            }
                            else
                            {
                                logger.Log("{0} got bad result for getlevel subscription from {1}", this.ToString(), senderPort.ToString());
                            }
                        }
                        break;
                    case RoleSwitchMultiLevel.OpGetName:
                        {
                            if (retVals.Count >= 1 && retVals[0].Value() != null)
                            {
                                double level = (double)retVals[0].Value();

                                registeredSwitches[senderPort].Level = level;
                            }
                            else
                            {
                                logger.Log("{0} got bad result for getlevel subscription from {1}", this.ToString(), senderPort.ToString());
                            }
                        }
                        break;
                    case RoleLightColor.OpGetName:
                        {
                            if (!registeredSwitches[senderPort].IsColored)
                            {
                                logger.Log("Got {0} for non-colored switch {1}", opName, senderPort.ToString());

                                return;
                            }

                            if (retVals.Count >= 3)
                            {
                                byte red, green, blue;

                                red = Math.Min(Math.Max((byte)(int)retVals[0].Value(), (byte)0), (byte)255);
                                green = Math.Min(Math.Max((byte)(int)retVals[1].Value(), (byte)0), (byte)255);
                                blue = Math.Min(Math.Max((byte)(int)retVals[2].Value(), (byte)0), (byte)255);

                                registeredSwitches[senderPort].Color = Color.FromArgb(red, green, blue);
                            }
                            else
                            {
                                logger.Log("{0} got bad result for getlevel subscription from {1}", this.ToString(), senderPort.ToString());
                            }
                        }
                        break;
                    default:
                        logger.Log("Got notification from incomprehensible operation: " + opName);
                        break;
                }
            }
        }
        void SmartHomeRun()
        {
            Console.WriteLine("SmartHome Run");
            string connStr = "Server=smarthomeapp.cloudapp.net;Database=smarthome;Uid=root;Pwd=dlsgh123!;Port=3306;";
            using (MySqlConnection conn = new MySqlConnection(connStr))
            {
                conn.Open();
                string sql = "SELECT * FROM list";

                //ExecuteReader를 이용하여
                //연결 모드로 데이타 가져오기
                MySqlCommand cmd = new MySqlCommand(sql, conn);
                MySqlDataReader rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    Console.WriteLine("{0} {1}: {2} {3}", rdr["number"], rdr["number"].GetType(), rdr["deviceName"], rdr["command"]);
                    string deviceName = String.Format("{0}", rdr["deviceName"]);
                    string command = String.Format("{0}", rdr["command"]);
                    if (deviceName == "HUE" && command == "1")
                    {
                        SetAllSwitches(1.0);
                        break;
                    }
                }
                rdr.Close();
            }
        }

        void timer_bluetooth_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            Console.WriteLine("timer_bluetooth_Elapsed");
            SetAllSwitches(0.0);
            ((System.Timers.Timer)sender).Stop();
            timer_bluetooth = null;
        }
        void timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (push_event_check == 1)
            {
                push_event_check = 2; timerCount = 0;
                recordingController = 2;
                Console.WriteLine(this.ToString() + " : stopped Recording");
            }
            ((System.Timers.Timer)sender).Stop();
        }
        void timer_motion_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (push_event_check == 2 || push_event_check == 0)
            {
                push_event_check = 1; timerCount = 0;
                recordingController = 1;
                Console.WriteLine(this.ToString() + " : Started Recording");
            }

            ((System.Timers.Timer)sender).Stop();
        }

        private void backgroundObjectDetector_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = sender as BackgroundWorker;

            do
            {
                bool foundObject = false;
                Rectangle rectObject = new Rectangle(0, 0, 0, 0);
                VPort cameraPort = (VPort)e.Argument;
                Bitmap image = null;

                lock (this)
                {
                    if (registeredCameras[cameraPort].BitmapImage != null)
                    {
                        try
                        {
                            image = (Bitmap)registeredCameras[cameraPort].BitmapImage.Clone();
                        }
                        catch (Exception)
                        {
                            logger.Log("BitmapImage Clone threw an exception!");
                        }
                        registeredCameras[cameraPort].BitmapImage.Dispose();
                        registeredCameras[cameraPort].BitmapImage = null;
                    }
                }

                if (null != image)
                {
                    foundObject = ExtractObjectFromFrame(image, cameraPort, ref rectObject);
                }

                lock (this)
                {
                    registeredCameras[cameraPort].ObjectFound = foundObject;
                    registeredCameras[cameraPort].LastObjectRect = rectObject;
                }

                if (null != image)
                {
                    image.Dispose();
                }
            }
            while (!worker.CancellationPending);

            e.Cancel = true;
        }


        //returns [cameraName, roleName] pairs
        public List<string> GetCameraList()
        {
            List<string> retList = new List<string>();

            lock (this)
            {
                foreach (var camera in cameraFriendlyNames.Keys)
                {
                    string bestRoleSoFar = RoleCamera.RoleName;

                    foreach (var role in cameraFriendlyNames[camera].GetInfo().GetRoles())
                    {
                        if (Role.ContainsRole(role.Name(), bestRoleSoFar))
                            bestRoleSoFar = role.Name();
                    }

                    retList.Add(camera);
                    retList.Add(bestRoleSoFar);
                }
            }

            return retList;
        }

        public void EnableMotionTrigger(string cameraFriendlyName, bool enable)
        {
            VPort cameraPort = cameraFriendlyNames[cameraFriendlyName];

            registeredCameras[cameraPort].EnableObjectTrigger = enable;


            // setup a background worker for object detection
            if (enable)
            {
                if (null == registeredCameras[cameraPort].BackgroundWorkerObjectDetector)
                {
                    registeredCameras[cameraPort].BackgroundWorkerObjectDetector = new BackgroundWorker();
                    registeredCameras[cameraPort].BackgroundWorkerObjectDetector.WorkerSupportsCancellation = true;
                    registeredCameras[cameraPort].BackgroundWorkerObjectDetector.DoWork +=
                        new DoWorkEventHandler(backgroundObjectDetector_DoWork);
                    registeredCameras[cameraPort].BackgroundWorkerObjectDetector.RunWorkerAsync(cameraPort);
                }
            }


            if (!enable)
            {
                if (registeredCameras[cameraPort].BackgroundWorkerObjectDetector != null)
                {
                    registeredCameras[cameraPort].BackgroundWorkerObjectDetector.CancelAsync();
                    registeredCameras[cameraPort].BackgroundWorkerObjectDetector = null;
                }

                StopRecording(cameraPort, true /* force */);
            }
        }



        public bool IsMotionTriggerEnabled(string cameraFriendlyName)
        {
            bool isEnabled = false;
            VPort cameraPort = cameraFriendlyNames[cameraFriendlyName];
            unsafe
            {
                isEnabled = registeredCameras[cameraPort].EnableObjectTrigger;
            }

            return isEnabled;
        }


        public void EnableVideoUpload(string cameraFriendlyName, bool enable)
        {
            VPort cameraPort = cameraFriendlyNames[cameraFriendlyName];

            registeredCameras[cameraPort].UploadVideo = enable;

            //If video upload is enabled then when recording is stopped program will upload video and snapshots

        }


        public bool IsVideoUploadEnabled(string cameraFriendlyName)
        {
            bool isVideoUploadEnabled = false;
            VPort cameraPort = cameraFriendlyNames[cameraFriendlyName];
            unsafe
            {
                isVideoUploadEnabled = registeredCameras[cameraPort].UploadVideo;
            }

            return isVideoUploadEnabled;
        }

        public List<string> GetReceivedMessages()
        {
            List<string> retList = new List<string>(this.receivedMessageList);
            retList.Reverse();
            return retList;
        }

        public string[] GetRecordedCamerasList()
        {

            //string directory = String.Format("{0}\\videos", moduleInfo.WorkingDir());
            //string[] cameraDirsArray = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);

            string[] cameraDirsArray = Directory.GetDirectories(this.videosDir, "*", SearchOption.TopDirectoryOnly);

            int count = 0;
            foreach (string cameraDir in cameraDirsArray)
            {

                int len = cameraDir.Length;
                int lastBackSlash = cameraDir.LastIndexOf('\\');
                if (lastBackSlash != len - 1)
                {
                    cameraDirsArray[count++] = cameraDir.Substring(lastBackSlash + 1, len - lastBackSlash - 1);
                }
            }

            return cameraDirsArray;
        }

        public int GetRecordedClipsCount(string cameraFriendlyName)
        {
            string directory = String.Format("{0}\\{1}", this.videosDir, cameraFriendlyName);

            string[] fileArray = Directory.GetFiles(directory, "*.mp4", SearchOption.AllDirectories);
            return fileArray.GetLength(0);
        }

        private bool IsFileLocked(string filePath)
        {
            bool locked = false;
            try
            {
                using (FileStream fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            }
            catch (Exception)
            {
                locked = true;
            }

            return locked;
        }

        string SMTPaddress = "smtp.gmail.com"; //SMTP 주소, 지메일 사용
        string SMTPid = "donnaknew"; // 지메일 아이디
        string SMTPpass = "zmfnqheps13"; //비번
        string senderID = "donnaknew@gmail.com"; //보내는 사람의 아이디
        string senderNAME = "SmartHome"; //보내는 사람에 표시될 이름
        string Tmail = "donnaknew@gmail.com";
        string Tsub = "Someone get in your Home";
        string Tbody = "Someone get in your Home";

        void SendMail()
        {
            try
            {
                MailMessage mail = new MailMessage();
                SmtpClient SmtpServer = new SmtpClient(SMTPaddress);
                mail.From = new MailAddress(senderID, senderNAME, System.Text.Encoding.UTF8);
                mail.To.Add(Tmail);
                mail.Subject = Tsub;
                mail.Body = Tbody;
                mail.BodyEncoding = System.Text.Encoding.UTF8;
                mail.SubjectEncoding = System.Text.Encoding.UTF8;

                SmtpServer.Port = 587;
                SmtpServer.Credentials = new System.Net.NetworkCredential(SMTPid, SMTPpass);

                SmtpServer.EnableSsl = true;
                SmtpServer.Send(mail);
                Console.WriteLine("메일발송 완료");
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }


        public string[] GetRecordedClips(string cameraFriendlyName, int countMax)
        {
            string directory = String.Format("{0}\\{1}", this.videosDir, cameraFriendlyName);
            string[] fileArray;

            try
            {
                fileArray = Directory.GetFiles(directory, "*.mp4", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                fileArray = new string[0];
            }

            List<string> fileExcludeList = new List<string>();

            // remove any clip that is still being written to
            for (int i = 0; i < fileArray.Length; ++i)
            {
                if (IsFileLocked(fileArray[i]))
                {
                    fileExcludeList.Add(fileArray[i]);
                }
            }

            for (int j = 0; j < fileExcludeList.Count; ++j)
            {
                fileArray = Array.FindAll<string>(fileArray, (f) => f != fileExcludeList[j]);
            }

            // sort the file array by their time stamp, earliest first
            Array.Sort<string>(fileArray, (f1, f2) => File.GetLastWriteTime(f1).CompareTo(File.GetLastWriteTime(f2)) * -1);

            ConvertLocalPathArrayToUrlArray(fileArray, countMax);

            if (fileArray.Length > countMax)
            {
                Array.Resize<string>(ref fileArray, countMax);
            }

            return fileArray;
        }

        // morph the fileArray into the externally accessible Url array
        private void ConvertLocalPathArrayToUrlArray(string[] fileArray, int countMax)
        {
            int count = 0;
            foreach (string filePath in fileArray)
            {
                int len = filePath.Length;
                string substr = /*"\\" +*/ VIDEO_SUB_DIR_NAME;
                int postVideosPosition = filePath.LastIndexOf(substr) + substr.Length + 1;
                fileArray[count++] = /*this.videosBaseUrl+*/ substr + "/" + filePath.Substring(postVideosPosition, len - postVideosPosition);
                fileArray[count - 1] = fileArray[count - 1].Replace("\\", "/");

                if (count == countMax) 
                    break;
            }
        }

        private string[] GetTriggerImagesFromClipUrl(string clipUrl)
        {
            // extract the sub string from the url that contains the relative location of the clip on disk
            // typical clip url is "http://<adddress:port>/<home id>/ArduinoCamApp/videos/<camera name>/<YYYY-MM-DD>/<hh-mm-ss>.mp4"

            //int idxRelClipPath = clipUrl.IndexOf(String.Format("/{0}/{1}/", moduleInfo.FriendlyName(), VIDEO_SUB_DIR_NAME));
            //int subStringLen = String.Format("{0}/{1}/", moduleInfo.FriendlyName(), VIDEO_SUB_DIR_NAME).Length;
            //string relClipPath = clipUrl.Substring(idxRelClipPath + subStringLen + 1);

            string relClipPath = clipUrl.Substring(this.videosBaseUrl.Length);

            relClipPath = relClipPath.Replace('/', '\\');
            string clipPath = String.Format("{0}\\{1}", this.videosDir, relClipPath);

            FileInfo clipFileInfo = new FileInfo(clipPath);
            string clipDirPath = clipFileInfo.DirectoryName;

            // get all jpgs in the same directory as the specified clip
            string[] triggerImagesArray = Directory.GetFiles(clipDirPath, "*.jpg", SearchOption.TopDirectoryOnly);

            // filter the jpeg files so that the ones whose write time is less than MAX_VIDEO_CLIP_LEN_IN_MINUTES minutes of the last
            // write time of the video clip 
            triggerImagesArray = Array.FindAll<string>(triggerImagesArray, (f) =>
                    (File.GetLastWriteTime(f).CompareTo(clipFileInfo.LastWriteTime) <= 0) &&
                    (File.GetLastWriteTime(f).CompareTo(clipFileInfo.LastWriteTime - DEFAULT_VIDEO_CLIP_LEN) >= 0));
            //(File.GetLastWriteTime(f).CompareTo(clipFileInfo.LastWriteTime - new TimeSpan(0, MAX_VIDEO_CLIP_LEN_IN_MINUTES, 0)) >= 0));

            // sort the file array by their time stamp, most recent first
            Array.Sort<string>(triggerImagesArray, (f1, f2) => File.GetLastWriteTime(f1).CompareTo(File.GetLastWriteTime(f2)) * -1);

            ConvertLocalPathArrayToUrlArray(triggerImagesArray, -1);

            return triggerImagesArray;
        }

        public int GetClipTriggerImagesCount(string clipUrl)
        {
            return GetTriggerImagesFromClipUrl(clipUrl).Length;
        }

        public string[] GetClipTriggerImages(string clipUrl, int countMax)
        {
            string[] triggerImagesArray = GetTriggerImagesFromClipUrl(clipUrl);

            if (triggerImagesArray.Length == 0)
            {
                return new string[0]; // don't return null for arrays
            }
            else if (countMax < triggerImagesArray.Length)
            {
                Array.Resize<string>(ref triggerImagesArray, countMax);
                return triggerImagesArray;
            }
            else  // countMax >= triggerImagesArray.Length
            {
                // don't fail for case when countMax is greater than the total count of clips
                // because it forces an additional async call in script which can be avoided
                return triggerImagesArray;
            }
        }


        public void SendMsgToCamera(string cameraControl, string cameraFriendlyName)
        {
            if (cameraFriendlyNames.ContainsKey(cameraFriendlyName))
            {
                VPort cameraPort = cameraFriendlyNames[cameraFriendlyName];

                if (registeredCameras.ContainsKey(cameraPort))
                {
                    IList<VParamType> retVal = null;

                    if (cameraControl.Equals(RolePTZCamera.OpZommOutName) || cameraControl.Equals(RolePTZCamera.OpZoomInName))
                    {
                        retVal = cameraPort.Invoke(RolePTZCamera.RoleName, cameraControl, new List<VParamType>(),
                               ControlPort, registeredCameras[cameraPort].Capability, ControlPortCapability);
                    }
                    else
                    {
                        retVal = cameraPort.Invoke(RolePTCamera.RoleName, cameraControl, new List<VParamType>(),
                                           ControlPort, registeredCameras[cameraPort].Capability, ControlPortCapability);
                    }

                    if (retVal.Count != 0 && retVal[0].Maintype() == (int)ParamType.SimpleType.error)
                    {
                        logger.Log("Got error while controlling camera {0} with controlType {1}: {2}", cameraFriendlyName, cameraControl, retVal[0].Value().ToString());
                    }

                }
            }
        }

        // Starts a new recording if there isn't one already under way
        private void StartRecording(VPort cameraPort, int videoWidth, int videoHeight, int videoFPSNum, int videoFPSDen, int videoEncBitrate)
        {
            if (registeredCameras[cameraPort].VideoWriter != null)
            {
                return;
            }

            logger.Log("Started new clip for {0}", cameraPort.GetInfo().GetFriendlyName());
            CameraInfo cameraInfo = registeredCameras[cameraPort];

            string fileName = GetMediaFileName(cameraPort.GetInfo().GetFriendlyName(), MediaType.MediaType_Video_MP4);
            uploadfilename = fileName;

            if (null == registeredCameras[cameraPort].VideoWriter)
            {
                registeredCameras[cameraPort].VideoWriter = new VideoWriter();
            }

            cameraInfo.CurrVideoStartTime = DateTime.Now;
            cameraInfo.CurrVideoEndTime = cameraInfo.CurrVideoStartTime + DEFAULT_VIDEO_CLIP_LEN;

            int result = cameraInfo.VideoWriter.Init(fileName, videoWidth, videoHeight, videoFPSNum, videoFPSDen, videoEncBitrate);

            if (result != 0)
            {
                string message = String.Format("Failed to start recording for {0} at {1}. Error code = {2}",
                                                cameraPort.GetInfo().GetFriendlyName(), DateTime.Now, result);
                logger.Log(message);
            }
        }

        private void StopRecording(VPort cameraPort, bool force)
        {
            bool stopConditionMet = false;
            CameraInfo cameraInfo = registeredCameras[cameraPort];

            //if ((DateTime.Now - registeredCameras[cameraPort].CurrVideoStartTime).TotalMinutes >=
            //            MAX_VIDEO_CLIP_LEN_IN_MINUTES)

            if (DateTime.Now >= registeredCameras[cameraPort].CurrVideoEndTime)
            {
                stopConditionMet = true;
            }

            if ((force || stopConditionMet) && (cameraInfo.VideoWriter != null))
            {
                string cameraName = cameraPort.GetInfo().GetFriendlyName();
                VideoWriter VideoWriter = cameraInfo.VideoWriter;

                SafeThread helper = new SafeThread(delegate() { StopRecordingHelper(VideoWriter, cameraName); },
                                                    "stoprecordinghelper-" + cameraName, logger);
                helper.Start();

                cameraInfo.RecordVideo = false;
                cameraInfo.VideoWriter = null;
                cameraInfo.CurrVideoStartTime = DateTime.MinValue;
                cameraInfo.CurrVideoEndTime = DateTime.MinValue;

                if (stopConditionMet)
                {
                    logger.Log("Stop recording because the clip time has elapsed for {0}",
                            cameraPort.GetInfo().GetFriendlyName());
                }
                else
                {
                    logger.Log("Stop recording for {0}", cameraPort.GetInfo().GetFriendlyName());
                }
            }
        }

        public void StartOrContinueRecording(string cameraFriendlyName)
        {
            VPort cameraPort = cameraFriendlyNames[cameraFriendlyName];

            var cameraInfo = registeredCameras[cameraPort];

            cameraInfo.RecordVideo = true;

            //if recording is going on, but the end time is less than clip length in the future, extend the end time
            if (cameraInfo.VideoWriter != null &&
                cameraInfo.CurrVideoEndTime < DateTime.Now + DEFAULT_VIDEO_CLIP_LEN)
            {
                cameraInfo.CurrVideoEndTime = DateTime.Now + DEFAULT_VIDEO_CLIP_LEN;
            }
        }

        private void StopRecordingHelper(VideoWriter VideoWriter, string cameraName)
        {
            DateTime startTime = DateTime.Now;
            int hresult = VideoWriter.Done();
            logger.Log("Stopping took {0} ms", (DateTime.Now - startTime).TotalMilliseconds.ToString());

            if (hresult != 0)
            {
                string message = String.Format("Failed to stop recording for {0} at {1}. Error code = {2:x}",
                                                cameraName, DateTime.Now, (uint)hresult);
                logger.Log(message);

            }

            logger.Log("stopped recording for {0}", cameraName);
            //UploadToAzure(uploadfilename);
        }

        public void StopRecording(string cameraFriendlyName)
        {
            VPort cameraPort = cameraFriendlyNames[cameraFriendlyName];
            StopRecording(cameraPort, true);
        }

        //called when the lock is acquired
        private void ForgetCamera(VPort cameraPort)
        {
            cameraFriendlyNames.Remove(cameraPort.GetInfo().GetFriendlyName());

            //stop recording if we have a video make object
            StopRecording(cameraPort, true);

            registeredCameras.Remove(cameraPort);

            logger.Log("{0} removed camera port {1}", this.ToString(), cameraPort.ToString());

        }

        //called when the lock is acquired and cameraPort is non-existent in the dictionary
        private void InitCamera(VPort cameraPort)
        {
            VCapability capability = GetCapability(cameraPort, Constants.UserSystem);

            //return if we didn't get a capability
            if (capability == null)
            {
                logger.Log("{0} didn't get a capability for {1}", this.ToString(), cameraPort.ToString());

                return;
            }

            //otherwise, add this to our list of cameras

            logger.Log("{0} adding camera port {1}", this.ToString(), cameraPort.ToString());

            CameraInfo cameraInfo = new CameraInfo();
            cameraInfo.Capability = capability;
            cameraInfo.LastImageBytes = new byte[0];
            cameraInfo.VideoWriter = null;
            cameraInfo.CurrVideoStartTime = DateTime.MinValue;
            cameraInfo.CurrVideoEndTime = DateTime.MinValue;

            registeredCameras.Add(cameraPort, cameraInfo);

            string cameraFriendlyName = cameraPort.GetInfo().GetFriendlyName();
            cameraFriendlyNames.Add(cameraFriendlyName, cameraPort);

            cameraPort.Subscribe(RoleCamera.RoleName, RoleCamera.OpGetVideo, ControlPort, cameraInfo.Capability, ControlPortCapability);
        }


        private bool ExtractObjectFromFrame(Bitmap image, VPort cameraPort, ref Rectangle rectObject)
        {
            bool foundObject = false;

            // Lock the bitmap's bits.  
            Rectangle rect = new Rectangle(0, 0, image.Width, image.Height);
            BitmapData bmpData = image.LockBits(rect, ImageLockMode.ReadOnly, image.PixelFormat);

            // Get the address of the first line.
            IntPtr ptr = bmpData.Scan0;

            if (null == registeredCameras[cameraPort].ObjectDetector)
            {
                registeredCameras[cameraPort].ObjectDetector = new ObjectDetector();
            }

            unsafe
            {
                if (!registeredCameras[cameraPort].ObjectDetector.IsInitialized())
                {
                    registeredCameras[cameraPort].ObjectDetector.InitializeFromFrame((byte*)ptr, 3 * image.Width * image.Height, image.Width, image.Height, null);
                }
                else
                {
                    rectObject = registeredCameras[cameraPort].ObjectDetector.GetObjectRect((byte*)ptr, 3 * image.Width * image.Height);
                    if (rectObject.Width != 0 && rectObject.Height != 0)
                        foundObject = true;
                }

                if (foundObject)
                {
                    logger.Log("Object detected by camera {0} with co-ordinates X={1}, Y={2}, Width={3}, Height={4}",
                        cameraPort.GetInfo().GetFriendlyName(), rectObject.X.ToString(), rectObject.Y.ToString(), rectObject.Width.ToString(), rectObject.Height.ToString());
                }
            }

            image.UnlockBits(bmpData);

            return foundObject;

        }

        private void WriteObjectImage(VPort cameraPort, Bitmap image, Rectangle rectSrc, bool center)
        {
            Rectangle rectTarget = rectSrc;
            int srcPixelShiftX = 0;
            int srcPixelShiftY = 0;

            if (rectSrc.Width == 0 && rectSrc.Height == 0)
            {
                logger.Log("Write Object Image Called with Rect with zero height and width!");
                return;
            }

            if (center)
            {
                rectTarget.X = (int)((image.Width - rectSrc.Width) / 2.0);
                rectTarget.Y = (int)((image.Height - rectSrc.Height) / 2.0);
                srcPixelShiftX = rectTarget.X - rectSrc.X;
                srcPixelShiftY = rectTarget.Y - rectSrc.Y;
            }

            // create the destination based upon layer one
            BitmapData bmpData = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = bmpData.Stride;
            image.UnlockBits(bmpData);

            WriteableBitmap composite = new WriteableBitmap(image.Width, image.Height, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null);
            Int32Rect sourceRect = new Int32Rect(0, 0, (int)image.Width, (int)image.Height);
            byte[] pixels = new byte[stride * image.Height];

            for (int x = 0; x < image.Width; ++x)
            {
                for (int y = 0; y < image.Height; ++y)
                {
                    if (rectSrc.Contains(x, y))
                    {
                        Color clr = image.GetPixel(x, y);
                        pixels[stride * (y + srcPixelShiftY) + 3 * (x + srcPixelShiftX)] = clr.R;
                        pixels[stride * (y + srcPixelShiftY) + 3 * (x + srcPixelShiftX) + 1] = clr.G;
                        pixels[stride * (y + srcPixelShiftY) + 3 * (x + srcPixelShiftX) + 2] = clr.B;
                    }
                    else if (!rectTarget.Contains(x, y))
                    {
                        pixels[stride * y + 3 * x] = 0x00;
                        pixels[stride * y + 3 * x + 1] = 0x00;
                        pixels[stride * y + 3 * x + 2] = 0x00;
                    }
                }
            }
            composite.WritePixels(sourceRect, pixels, stride, 0);

            // encode the bitmap to the output file
            JpegBitmapEncoder encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(composite));
            string filepath = GetMediaFileName(cameraPort.GetInfo().GetFriendlyName(), MediaType.MediaType_Image_JPEG);
            logger.Log("filepath to save image: {0}", filepath);

            if (null == filepath)
            {
                logger.Log("GetMediaFileName failed to get a file name, are there more than 10 files of the same name?");
                return;
            }

            using (var stream = new FileStream(filepath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                encoder.Save(stream);
            }
        }

        private void AddFrameToVideo(Bitmap image, VPort cameraPort, long sampleTime)
        {
            // Lock the bitmap's bits.  
            Rectangle rect = new Rectangle(0, 0, image.Width, image.Height);
            BitmapData bmpData = image.LockBits(rect, ImageLockMode.ReadOnly, image.PixelFormat);

            // Get the address of the first line.
            IntPtr ptr = bmpData.Scan0;

            int result;

            unsafe
            {
                result = registeredCameras[cameraPort].VideoWriter.AddFrame((byte*)ptr, 3 * image.Width * image.Height, image.Width, image.Height, sampleTime);
            }

            image.UnlockBits(bmpData);

            if (result != 0)
            {
                string message = String.Format("Failed to add frame for {0}. ResultCode: {1:x}", cameraPort.GetInfo().GetFriendlyName(), ((uint)result));
                logger.Log(message);

            }
        }

        private string GetMediaFileName(string cameraName, MediaType mediaType)
        {
            DateTime currTime = DateTime.Now;

            string directory = String.Format("{0}\\{1}\\{2}-{3}-{4}", this.videosDir, cameraName, currTime.Year, currTime.Month, currTime.Day);

            //this method does nothing if the directory exists
            Directory.CreateDirectory(directory);

            string fileName = "";

            if (mediaType == MediaType.MediaType_Video_MP4)
            {
                fileName = String.Format("{0}\\{1}-{2}-{3}.mp4", directory, currTime.Hour, currTime.Minute, currTime.Second);
            }
            else if (mediaType == MediaType.MediaType_Image_JPEG)
            {
                fileName = String.Format("{0}\\{1}-{2}-{3}.jpg", directory, currTime.Hour, currTime.Minute, currTime.Second);
            }

            int count = 1;
            while (File.Exists(fileName) && count <= 10)
            {
                logger.Log("duplicate filename {0}", fileName);

                if (mediaType == MediaType.MediaType_Video_MP4)
                {
                    fileName = String.Format("{0}\\{1}-{2}-{3}_{4}.mp4", directory, currTime.Hour, currTime.Minute, currTime.Second, count);
                }
                else if (mediaType == MediaType.MediaType_Image_JPEG)
                {
                    fileName = String.Format("{0}\\{1}-{2}-{3}_{4}.jpg", directory, currTime.Hour, currTime.Minute, currTime.Second, count);
                }
                count++;
            }

            if (File.Exists(fileName))
            {
                logger.Log("could find a valid file name.");
                return null;
            }

            return fileName;

        }

        private void Log(string format, params string[] args)
        {
            logger.Log(format, args);
        }

        private string GetLocalHostIpAddress()
        {
            string ipAddress = null;
            IPAddress[] ips;

            ips = Dns.GetHostAddresses(Dns.GetHostName());

            foreach (IPAddress ip in ips)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    ipAddress = ip.ToString();
                    break;
                }
            }

            return ipAddress;
        }

        public bool IsMotionedTriggerEnabled(string cameraFriendlyName)
        {
            return this.IsMotionTriggerEnabled(cameraFriendlyName);
        }

        public List<List<string>> GetDeviceList()
        {

            string connStr = "Server=smarthomeapp.cloudapp.net;Database=smarthome;Uid=root;Pwd=dlsgh123!;Port=3306;";
            List<List<string>> retVal = new List<List<string>>();
            using (MySqlConnection conn = new MySqlConnection(connStr))
            {
                conn.Open();
                string sql = "SELECT * FROM list";

                //ExecuteReader를 이용하여
                //연결 모드로 데이타 가져오기
                MySqlCommand cmd = new MySqlCommand(sql, conn);
                MySqlDataReader rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    Console.WriteLine("{0}: {1} :{2}", rdr["number"], rdr["deviceName"], rdr["command"]);
                    List<string> device = new List<string>();
                    device.Add(rdr["number"].ToString());
                    device.Add(rdr["deviceName"].ToString());
                    device.Add(rdr["command"].ToString());
                    retVal.Add(device);
                }
                rdr.Close();
            }
            return retVal;
        }

        internal double GetLevel(string switchFriendlyName)
        {
            if (switchFriendlyNames.ContainsKey(switchFriendlyName))
                return registeredSwitches[switchFriendlyNames[switchFriendlyName]].Level;

            return 0;
        }

        void InitSwitch(VPort switchPort, SwitchType switchType, bool isColored)
        {

            logger.Log("{0} adding switch {1} {2}", this.ToString(), switchType.ToString(), switchPort.ToString());

            SwitchInfo switchInfo = new SwitchInfo();
            switchInfo.Capability = GetCapability(switchPort, Constants.UserSystem);
            switchInfo.Level = 0;
            switchInfo.Type = switchType;

            switchInfo.IsColored = isColored;
            switchInfo.Color = Color.Black;

            registeredSwitches.Add(switchPort, switchInfo);

            string switchFriendlyName = switchPort.GetInfo().GetFriendlyName();
            switchFriendlyNames.Add(switchFriendlyName, switchPort);

            if (switchInfo.Capability != null)
            {
                IList<VParamType> retVals;

                if (switchType == SwitchType.Multi)
                {
                    retVals = switchPort.Invoke(RoleSwitchMultiLevel.RoleName, RoleSwitchMultiLevel.OpGetName, null,
                    ControlPort, switchInfo.Capability, ControlPortCapability);

                    switchPort.Subscribe(RoleSwitchMultiLevel.RoleName, RoleSwitchMultiLevel.OpGetName, ControlPort, switchInfo.Capability, ControlPortCapability);

                    if (retVals[0].Maintype() < 0)
                    {
                        logger.Log("SwitchController could not get current level for {0}", switchFriendlyName);
                    }
                    else
                    {
                        switchInfo.Level = (double)retVals[0].Value();
                    }
                }
                else
                {
                    retVals = switchPort.Invoke(RoleSwitchBinary.RoleName, RoleSwitchBinary.OpGetName, null,
                    ControlPort, switchInfo.Capability, ControlPortCapability);

                    switchPort.Subscribe(RoleSwitchBinary.RoleName, RoleSwitchBinary.OpGetName, ControlPort, switchInfo.Capability, ControlPortCapability);

                    if (retVals[0].Maintype() < 0)
                    {
                        logger.Log("SwitchController could not get current level for {0}", switchFriendlyName);
                    }
                    else
                    {
                        bool boolLevel = (bool)retVals[0].Value();
                        switchInfo.Level = (boolLevel) ? 1 : 0;
                    }
                }

                //fix the color up now

                if (isColored)
                {
                    var retValsColor = switchPort.Invoke(RoleLightColor.RoleName, RoleLightColor.OpGetName, null,
                                                          ControlPort, switchInfo.Capability, ControlPortCapability);

                    switchPort.Subscribe(RoleLightColor.RoleName, RoleLightColor.OpGetName, ControlPort, switchInfo.Capability, ControlPortCapability);

                    if (retVals[0].Maintype() < 0)
                    {
                        logger.Log("SwitchController could not get color for {0}", switchFriendlyName);
                    }
                    else
                    {
                        byte red, green, blue;

                        red = Math.Min(Math.Max((byte)(int)retValsColor[0].Value(), (byte)0), (byte)255);
                        green = Math.Min(Math.Max((byte)(int)retValsColor[1].Value(), (byte)0), (byte)255);
                        blue = Math.Min(Math.Max((byte)(int)retValsColor[2].Value(), (byte)0), (byte)255);

                        switchInfo.Color = Color.FromArgb(red, green, blue);
                    }
                }
            }
        }

        void ForgetSwitch(VPort switchPort)
        {
            switchFriendlyNames.Remove(switchPort.GetInfo().GetFriendlyName());

            registeredSwitches.Remove(switchPort);

            logger.Log("{0} removed switch/light port {1}", this.ToString(), switchPort.ToString());
        }

        internal void DiscoSwitches()
        {
            //do the first color change
            SetDiscoColor();

            // Set the Interval to 2 seconds (2000 milliseconds).
            discoTimer = new System.Timers.Timer(2000);
            // Hook up the Elapsed event for the timer.
            discoTimer.Elapsed += new ElapsedEventHandler(OnDiscoEvent);
            discoTimer.Start();
        }

        //Called by timer 5 times
        private void OnDiscoEvent(object source, ElapsedEventArgs e)
        {
            if (countDiscoEvents <= maxDiscoEvents)
            {
                countDiscoEvents++;
                SetDiscoColor();
            }
            else
            {
                discoTimer.Stop();
                countDiscoEvents = 0;
            }
        }

        private void SetDiscoColor()
        {
            var r = new Random(); //used to randomly pick color from array of colors

            foreach (KeyValuePair<string, VPort> switchs in switchFriendlyNames)
            {
                //check if switch has color and then set it.
                if (registeredSwitches[switchs.Value].IsColored)
                {
                    Color c = colorChoices[r.Next(0, colorChoices.Length - 1)];
                    SetColor(switchs.Key.ToString(), c);
                }
            }
        }

        internal void SetLevel(string switchFriendlyName, double level)
        {
            if (switchFriendlyNames.ContainsKey(switchFriendlyName))
            {
                VPort switchPort = switchFriendlyNames[switchFriendlyName];

                if (registeredSwitches.ContainsKey(switchPort))
                {
                    SwitchInfo switchInfo = registeredSwitches[switchPort];

                    IList<VParamType> args = new List<VParamType>();

                    //make sure that the level is between zero and 1
                    if (level < 0) level = 0;
                    if (level > 1) level = 1;

                    if (switchInfo.Type == SwitchType.Multi)
                    {
                        var retVal = Invoke(switchPort, RoleSwitchMultiLevel.Instance, RoleSwitchMultiLevel.OpSetName, new ParamType(level));

                        if (retVal != null && retVal.Count == 1 && retVal[0].Maintype() == (int)ParamType.SimpleType.error)
                        {
                            logger.Log("Error in setting level: {0}", retVal[0].Value().ToString());

                            throw new Exception(retVal[0].Value().ToString());
                        }
                    }
                    else
                    {
                        //interpret all non-zero values as ON
                        bool boolLevel = (level > 0) ? true : false;

                        var retVal = Invoke(switchPort, RoleSwitchBinary.Instance, RoleSwitchBinary.OpSetName, new ParamType(boolLevel));

                        if (retVal != null && retVal.Count == 1 && retVal[0].Maintype() == (int)ParamType.SimpleType.error)
                        {
                            logger.Log("Error in setting level: {0}", retVal[0].Value().ToString());

                            throw new Exception(retVal[0].Value().ToString());
                        }

                    }

                    lock (this)
                    {
                        this.lastSet = DateTime.Now;
                    }

                    switchInfo.Level = level;
                }
            }
            else
            {
                throw new Exception("Switch with friendly name " + switchFriendlyName + " not found");
            }
        }

        internal void SetAllSwitches(double level)
        {
            foreach (KeyValuePair<string, VPort> switchs in switchFriendlyNames)
            {
                SetLevel(switchs.Key.ToString(), level);
            }
        }

        internal void SetColor(string switchFriendlyName, Color color)
        {
            if (switchFriendlyNames.ContainsKey(switchFriendlyName))
            {
                VPort switchPort = switchFriendlyNames[switchFriendlyName];

                if (registeredSwitches.ContainsKey(switchPort))
                {
                    SwitchInfo switchInfo = registeredSwitches[switchPort];

                    if (!switchInfo.IsColored)
                        throw new Exception("SetColor called on non-color switch " + switchFriendlyName);

                    IList<VParamType> args = new List<VParamType>();

                    var retVal = Invoke(switchPort, RoleLightColor.Instance, RoleLightColor.OpSetName,
                                        new ParamType(color.R), new ParamType(color.G), new ParamType(color.B));

                    if (retVal != null && retVal.Count == 1 && retVal[0].Maintype() == (int)ParamType.SimpleType.error)
                    {
                        logger.Log("Error in setting color: {0}", retVal[0].Value().ToString());
                        throw new Exception(retVal[0].Value().ToString());
                    }

                    lock (this)
                    {
                        this.lastSet = DateTime.Now;
                    }

                    switchInfo.Color = color;
                }
                else
                {
                    throw new Exception("Switch with friendly name " + switchFriendlyName + " is not registered");
                }
            }
            else
            {
                throw new Exception("Switch with friendly name " + switchFriendlyName + " not found");
            }
        }

        internal Color GetColor(string switchFriendlyName)
        {
            if (switchFriendlyNames.ContainsKey(switchFriendlyName))
            {
                VPort switchPort = switchFriendlyNames[switchFriendlyName];

                if (registeredSwitches.ContainsKey(switchPort))
                {
                    SwitchInfo switchInfo = registeredSwitches[switchPort];

                    if (!switchInfo.IsColored)
                        throw new Exception("GetColor called on non-color switch " + switchFriendlyName);

                    return switchInfo.Color;
                }
                else
                {
                    throw new Exception("Switch with friendly name " + switchFriendlyName + " is not registered");
                }
            }
            else
            {
                throw new Exception("Switch with friendly name " + switchFriendlyName + " not found");
            }
        }



        //returns a 8-tuple for each switch: (name, location, type, level, isColored, red, green, blue)
        internal List<string> GetAllSwitches()
        {
            List<string> retList = new List<string>();

            foreach (string friendlyName in switchFriendlyNames.Keys)
            {
                VPort switchPort = switchFriendlyNames[friendlyName];
                SwitchInfo switchInfo = registeredSwitches[switchPort];

                retList.Add(friendlyName);
                retList.Add(switchPort.GetInfo().GetLocation().ToString());
                retList.Add(switchInfo.Type.ToString());
                retList.Add(switchInfo.Level.ToString());

                retList.Add(switchInfo.IsColored.ToString());
                retList.Add(switchInfo.Color.R.ToString());
                retList.Add(switchInfo.Color.G.ToString());
                retList.Add(switchInfo.Color.B.ToString());
            }

            return retList;
        }


    }
}