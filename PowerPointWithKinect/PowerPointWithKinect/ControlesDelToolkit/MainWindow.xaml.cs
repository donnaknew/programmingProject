using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Forms;
using Microsoft.Kinect;
using Microsoft.Kinect.Toolkit;
using Microsoft.Kinect.Toolkit.Controls;
using System.Runtime.InteropServices;
using System.Threading;

namespace ControlesDelToolkit
{
    public partial class MainWindow : Window
    {
        //Kinect Object
        KinectSensorChooser KinectSensor;
        KinectSensor Kinect;

        //Hand Grip State var.
        int griped = 0;

        //Clicked State var.
        bool clicked = false;

        WriteableBitmap bitmapImagenColor = null;
        byte[] bytesColor;
        Skeleton[] skeleton = null;

        bool ForwardActiveMovement = false;
        bool BackActiveMovement = false;
        bool selectUpDown = false;

        SolidColorBrush brushActive = new SolidColorBrush(Colors.Green);
        SolidColorBrush brushOff = new SolidColorBrush(Colors.Red);

        //Import DLL for mouse event
        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")]
        public static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);
        public const int WM_LBUTTONDOWN = 0x0002, WM_LBUTTONUP = 0x0004, WM_RBUTTONDOWN = 0x0008, WM_RBUTTONUP = 0x0010;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded_1(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Main Windows Loading...");
            //Kinect Event Handler Register
            KinectSensor = new KinectSensorChooser();
            KinectSensor.KinectChanged += KinectSensor_KinectChanged;
            sensorChooserUI.KinectSensorChooser = KinectSensor;
            KinectSensor.Start();
        }

        void KinectSensor_KinectChanged(object sender, KinectChangedEventArgs e)
        {
            bool error = true;

            if (e.OldSensor == null)
            {
                try
                {
                    e.OldSensor.DepthStream.Disable();
                    e.OldSensor.SkeletonStream.Disable();
                }
                catch (Exception)
                {
                    error = true;
                }
            }

            if (e.NewSensor == null)
                return;

            try
            {
                Kinect = e.NewSensor;
                e.NewSensor.DepthStream.Enable(DepthImageFormat.Resolution640x480Fps30);
                e.NewSensor.SkeletonStream.Enable();
                e.NewSensor.SkeletonFrameReady += KinectSensor_SkeletonFrameReady;


                try
                {
                    e.NewSensor.SkeletonStream.TrackingMode = SkeletonTrackingMode.Seated;
                    e.NewSensor.DepthStream.Range = DepthRange.Near;
                    e.NewSensor.SkeletonStream.EnableTrackingInNearRange = true;
                }
                catch (InvalidOperationException)
                {
                    e.NewSensor.DepthStream.Range = DepthRange.Default;
                    e.NewSensor.SkeletonStream.EnableTrackingInNearRange = false;
                }
            }
            catch (InvalidOperationException)
            {
                error = true;
            }

            ZonaCursor.KinectSensor = e.NewSensor;
            KinectRegion.AddHandPointerGripHandler(this.ZonaCursor, this.OnHandGripHandler);
            KinectRegion.AddHandPointerGripReleaseHandler(this.ZonaCursor, this.OngRripReleaseHandler);
            KinectRegion.AddHandPointerMoveHandler(this.ZonaCursor, this.OnMoveHandler);
            KinectRegion.AddHandPointerPressHandler(this.ZonaCursor, this.OnPressHandler);
            KinectRegion.AddHandPointerPressReleaseHandler(this.ZonaCursor, this.OnPressRelaaseHandler);

        }
        private void OnPressRelaaseHandler(object sender, HandPointerEventArgs handPointerEventArgs)
        {
            /*** ***/
        }

        private void OnPressHandler(object sender, HandPointerEventArgs handPointerEventArgs)
        {

            mouse_event(WM_RBUTTONDOWN, 0, 0, 0, 0);
            Thread.Sleep(50);
            mouse_event(WM_RBUTTONUP, 0, 0, 0, 0);
            Thread.Sleep(100);
        }

        private void OnMoveHandler(object sender, HandPointerEventArgs handPointerEventArgs)
        {
            if (griped == 0)
            {
                mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0);
            }
            else if (griped == 1)
            {
                mouse_event(WM_LBUTTONUP, 0, 0, 0, 0);

                griped = -1;
            }

            var xPosition = handPointerEventArgs.HandPointer.GetPosition(this.ZonaCursor).X;
            var yPosition = handPointerEventArgs.HandPointer.GetPosition(this.ZonaCursor).Y;
           Console.WriteLine("x :" + xPosition + "y" + yPosition);
            SetCursorPos((int)xPosition, (int)yPosition);


        }
        private void OngRripReleaseHandler(object sender, HandPointerEventArgs handPointerEventArgs)
        {
            griped = 1;
        }

        private void OnHandGripHandler(object sender, HandPointerEventArgs handPointerEventArgs)
        {
            griped = 0;
        }

        void KinectSensor_SkeletonFrameReady(object sender, SkeletonFrameReadyEventArgs e)
        {
            using (SkeletonFrame frame = e.OpenSkeletonFrame())
            {
                if (frame != null)
                {
                    skeleton = new Skeleton[frame.SkeletonArrayLength];
                    frame.CopySkeletonDataTo(skeleton);
                }
            }

            if (skeleton == null) return;

            Skeleton skeletonClose = skeleton.Where(s => s.TrackingState == SkeletonTrackingState.Tracked)
                                                 .OrderBy(s => s.Position.Z * Math.Abs(s.Position.X))
                                                 .FirstOrDefault();

            if (skeletonClose == null) return;

            var head = skeletonClose.Joints[JointType.Head];
            var HandRight = skeletonClose.Joints[JointType.HandRight];
            var HandLeft = skeletonClose.Joints[JointType.HandLeft];

            if (head.TrackingState == JointTrackingState.NotTracked ||
                HandRight.TrackingState == JointTrackingState.NotTracked ||
                HandLeft.TrackingState == JointTrackingState.NotTracked)
            {
                return;
            }

            processBackFoward(head, HandRight, HandLeft);
        }

        private void processBackFoward(Joint head, Joint HandRight, Joint HandLeft)
        {


            if (HandRight.Position.X > head.Position.X + 0.45)
            {
                if (!ForwardActiveMovement)
                {
                    ForwardActiveMovement = true;
                   Console.WriteLine("Right");
                    System.Windows.Forms.SendKeys.SendWait("{Right}");
                }
            }
            else
            {
                ForwardActiveMovement = false;
            }

            if (HandLeft.Position.X < head.Position.X - 0.45)
            {
                if (!BackActiveMovement)
                {
                    BackActiveMovement = true;
                    Console.WriteLine("Left");
                    System.Windows.Forms.SendKeys.SendWait("{Left}");
                }
            }
            else
            {
                BackActiveMovement = false;
            }

            if (HandLeft.Position.Y > head.Position.Y + 0.12)
            {
                if (!selectUpDown)
                {
                    selectUpDown = true;
                    Console.WriteLine("HandLeft Over the head");
                    System.Windows.Forms.SendKeys.SendWait("^p");
                }
            }
            else if (HandRight.Position.Y > head.Position.Y + 0.12)
            {
                if (!selectUpDown)
                {
                    selectUpDown = true;
                    Console.WriteLine("HandRight Over the right");
                    System.Windows.Forms.SendKeys.SendWait("^a");
                }
            }
            else
            {
                selectUpDown = false;
            }

        }
    }
}
