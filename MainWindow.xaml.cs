//------------------------------------------------------------------------------
// <copyright file="MainWindow.xaml.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

// This module contains code to do Kinect NUI initialization,
// processing, displaying players on screen, and sending updated player
// positions to the game portion for hit testing.

namespace ShapeGame
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Media;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Data;
    using System.Windows.Threading;
    using ShapeGame.Utils;
    using Ventuz.OSC;
    using System.Windows.Media.Imaging;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 
    public partial class MainWindow : Window
    {
        

        #region Private State
        private const int TimerResolution = 2;  // ms
        private const int NumIntraFrames = 3;
        private const int MaxShapes = 80;
        private const double MaxFramerate = 70;
        private const double MinFramerate = 15;
        private const double MinShapeSize = 12;
        private const double MaxShapeSize = 90;
        private const double DefaultDropRate = 2.5;
        private const double DefaultDropSize = 32.0;
        private const double DefaultDropGravity = 1.0;

        private readonly Dictionary<int, Player> players = new Dictionary<int, Player>();
        private readonly SoundPlayer popSound = new SoundPlayer();
        private readonly SoundPlayer hitSound = new SoundPlayer();
        private readonly SoundPlayer squeezeSound = new SoundPlayer();


        private double dropRate = DefaultDropRate;
        private double dropSize = DefaultDropSize;
        private double dropGravity = DefaultDropGravity;
        private DateTime lastFrameDrawn = DateTime.MinValue;
        private DateTime predNextFrame = DateTime.MinValue;
        private double actualFrameTime;


        // Player(s) placement in scene (z collapsed):
        private Rect playerBounds;
        private Rect screenRect;

        private double targetFramerate = MaxFramerate;
        private int frameCount;
        private bool runningGameThread;
        private FallingThings myFallingThings;
        private int playersAlive;

        private UdpReader OscReader;
        private int port = 3002;
        private SkeletonDataSource skeleton;
        private DispatcherTimer timerUDP;
        #endregion Private State

        #region ctor + Window Events

        public MainWindow()
        {
            InitializeComponent();
            
            //Init Upd port
            OscReader = new UdpReader(port);
            
            //Init connection with server
            InitConnection();            
        }

        private void InitConnection() {
            //Init Timer
            this.timerUDP = new DispatcherTimer();
            this.timerUDP.Interval = TimeSpan.FromMilliseconds(1000 / 30);
            this.timerUDP.Start();
            this.timerUDP.Tick += timerKinect_Tick;
        }

        // Since the timer resolution defaults to about 10ms precisely, we need to
        // increase the resolution to get framerates above between 50fps with any
        // consistency.
        [DllImport("Winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern int TimeBeginPeriod(uint period);

        private void RestoreWindowState()
        {
            // Restore window state to that last used
            Rect bounds = Properties.Settings.Default.PrevWinPosition;
            if (bounds.Right != bounds.Left)
            {
                this.Top = bounds.Top;
                this.Left = bounds.Left;
                this.Height = bounds.Height;
                this.Width = bounds.Width;
            }

            this.WindowState = (WindowState)Properties.Settings.Default.WindowState;
        }

        private void WindowLoaded(object sender, EventArgs e)
        {
            playfield.ClipToBounds = true;

            this.myFallingThings = new FallingThings(MaxShapes, this.targetFramerate, NumIntraFrames);

            this.UpdatePlayfieldSize();

            this.myFallingThings.SetGravity(this.dropGravity);
            this.myFallingThings.SetDropRate(this.dropRate);
            this.myFallingThings.SetSize(this.dropSize);
            this.myFallingThings.SetPolies(PolyType.All);
            this.myFallingThings.SetGameMode(GameMode.Off);

            this.popSound.Stream = Properties.Resources.Pop_5;
            this.hitSound.Stream = Properties.Resources.Hit_2;
            this.squeezeSound.Stream = Properties.Resources.Squeeze;

            this.popSound.Play();

            TimeBeginPeriod(TimerResolution);
            var myGameThread = new Thread(this.GameThread);
            myGameThread.SetApartmentState(ApartmentState.STA);
            myGameThread.Start();

            FlyingText.NewFlyingText(this.screenRect.Width / 30, new Point(this.screenRect.Width / 2, this.screenRect.Height / 2), "Shapes!");
        }

        private void WindowClosing(object sender, CancelEventArgs e)
        {
            //cerrar la conexion con el server

            this.runningGameThread = false;
            Properties.Settings.Default.PrevWinPosition = this.RestoreBounds;
            Properties.Settings.Default.WindowState = (int)this.WindowState;
            Properties.Settings.Default.Save();

            OscReader.Dispose();
            popSound.Dispose();
            hitSound.Dispose();
            squeezeSound.Dispose();

        //    Console.WriteLine("total frames: " + countFrames);
        }

        private void WindowClosed(object sender, EventArgs e)
        {

        }

        int countFrames = 0;
        void ParseMessages()
        {
            OscMessage message = OscReader.Receive();
            // Return if there are no more messages available
            if (message == null) return;
            OscBundle bundle = message as OscBundle;
            if (bundle == null) return;

            foreach (OscElement element in bundle.Elements)
            {

                string FPS = "FPS " + (string)element.Args[5];
                if (element.Address.Contains("Skeleton"))
                {
                    string hands = (string)element.Args[3];
                    string gesto = (string)element.Args[2];
                    if (gesto != "")
                    {
                        string gesture = gesto;

                    }
                    string dato = (string)element.Args[1];
                    string time = (string)element.Args[0];
                    skeleton = SkeletonParser.parse(dato);
                    UpdatePlayer(skeleton);
                }

                if (element.Address.Contains("/video/color"))
                {
                    countFrames++;
                    //int count = (int)element.Args[1];
                    byte[] bytesReceived = (byte[])element.Args[4];//new byte[total];

                    //Memory stream to store the bitmap data.
                    System.IO.MemoryStream ms = new System.IO.MemoryStream(bytesReceived);
                   
                    var imageSource = new BitmapImage();
                    imageSource.BeginInit();
                    imageSource.StreamSource = ms;
                    imageSource.EndInit();

                    // Assign the Source property of your image
                    this.imgRGB.Source = imageSource;
                   // tex.LoadImage(ms.ToArray());
                  //  ms.Dispose();
                }

            }

        }



        private void UpdatePlayer(SkeletonDataSource skeletonData)
        {
            int skeletonSlot = 0;

            foreach (SkeletonsData skeleton in skeletonData.getSkeletons())
            {
               // if (SkeletonTrackingState.Tracked == skeleton.TrackingState)
              //  {
                    Player player;
                    if (this.players.ContainsKey(skeletonSlot))
                    {
                        player = this.players[skeletonSlot];
                    }
                    else
                    {
                        player = new Player(skeletonSlot);
                        player.SetBounds(this.playerBounds);
                        this.players.Add(skeletonSlot, player);
                    }

                    player.LastUpdated = DateTime.Now;

                    // Update player's bone and joint positions
                    if (skeleton.getSkeletonJoint().Count > 0)
                    {
                        player.IsAlive = true;

                        // Head, hands, feet (hit testing happens in order here)
                        player.UpdateJointPosition(skeleton.getSkeletonJoint(), JointType.Head);
                        player.UpdateJointPosition(skeleton.getSkeletonJoint(), JointType.HandLeft);
                        player.UpdateJointPosition(skeleton.getSkeletonJoint(), JointType.HandRight);
                        player.UpdateJointPosition(skeleton.getSkeletonJoint(), JointType.FootLeft);
                        player.UpdateJointPosition(skeleton.getSkeletonJoint(), JointType.FootRight);

                        // Hands and arms
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.HandRight, JointType.WristRight);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.WristRight, JointType.ElbowRight);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.ElbowRight, JointType.ShoulderRight);

                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.HandLeft, JointType.WristLeft);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.WristLeft, JointType.ElbowLeft);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.ElbowLeft, JointType.ShoulderLeft);

                        // Head and Shoulders
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.ShoulderCenter, JointType.Head);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.ShoulderLeft, JointType.ShoulderCenter);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.ShoulderCenter, JointType.ShoulderRight);

                        // Legs
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.HipLeft, JointType.KneeLeft);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.KneeLeft, JointType.AnkleLeft);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.AnkleLeft, JointType.FootLeft);

                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.HipRight, JointType.KneeRight);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.KneeRight, JointType.AnkleRight);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.AnkleRight, JointType.FootRight);

                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.HipLeft, JointType.HipCenter);
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.HipCenter, JointType.HipRight);

                        // Spine
                        player.UpdateBonePosition(skeleton.getSkeletonJoint(), JointType.HipCenter, JointType.ShoulderCenter);
                  //  }
                }

                skeletonSlot++;
            }

        }

        void timerKinect_Tick(object sender, EventArgs e)
        {
            ParseMessages();
        }

       

        #endregion ctor + Window Events



        #region Kinect Skeleton processing

        private void CheckPlayers()
        {
            foreach (var player in this.players)
            {
                if (!player.Value.IsAlive)
                {
                    // Player left scene since we aren't tracking it anymore, so remove from dictionary
                    this.players.Remove(player.Value.GetId());
                    break;
                }
            }

            // Count alive players
            int alive = this.players.Count(player => player.Value.IsAlive);

            if (alive != this.playersAlive)
            {
                if (alive == 2)
                {
                    this.myFallingThings.SetGameMode(GameMode.TwoPlayer);
                }
                else if (alive == 1)
                {
                    this.myFallingThings.SetGameMode(GameMode.Solo);
                }
                else if (alive == 0)
                {
                    this.myFallingThings.SetGameMode(GameMode.Off);
                }

                if ((this.playersAlive == 0))
                {
                    BannerText.NewBanner(
                        Properties.Resources.Vocabulary,
                        this.screenRect,
                        true,
                        System.Windows.Media.Color.FromArgb(200, 255, 255, 255));
                }

                this.playersAlive = alive;
            }
        }

        private void PlayfieldSizeChanged(object sender, SizeChangedEventArgs e)
        {
            this.UpdatePlayfieldSize();
        }

        private void UpdatePlayfieldSize()
        {
            // Size of player wrt size of playfield, putting ourselves low on the screen.
            this.screenRect.X = 0;
            this.screenRect.Y = 0;
            this.screenRect.Width = this.playfield.ActualWidth;
            this.screenRect.Height = this.playfield.ActualHeight;

            BannerText.UpdateBounds(this.screenRect);

            this.playerBounds.X = 0;
            this.playerBounds.Width = this.playfield.ActualWidth;
            this.playerBounds.Y = this.playfield.ActualHeight * 0.2;
            this.playerBounds.Height = this.playfield.ActualHeight * 0.75;

            foreach (var player in this.players)
            {
                player.Value.SetBounds(this.playerBounds);
            }

            Rect fallingBounds = this.playerBounds;
            fallingBounds.Y = 0;
            fallingBounds.Height = playfield.ActualHeight;
            if (this.myFallingThings != null)
            {
                this.myFallingThings.SetBoundaries(fallingBounds);
            }
        }
        #endregion Kinect Skeleton processing

        #region GameTimer/Thread
        private void GameThread()
        {
            this.runningGameThread = true;
            this.predNextFrame = DateTime.Now;
            this.actualFrameTime = 1000.0 / this.targetFramerate;

            // Try to dispatch at as constant of a framerate as possible by sleeping just enough since
            // the last time we dispatched.
            while (this.runningGameThread)
            {
                // Calculate average framerate.  
                DateTime now = DateTime.Now;
                if (this.lastFrameDrawn == DateTime.MinValue)
                {
                    this.lastFrameDrawn = now;
                }

                double ms = now.Subtract(this.lastFrameDrawn).TotalMilliseconds;
                this.actualFrameTime = (this.actualFrameTime * 0.95) + (0.05 * ms);
                this.lastFrameDrawn = now;

                // Adjust target framerate down if we're not achieving that rate
                this.frameCount++;
                if ((this.frameCount % 100 == 0) && (1000.0 / this.actualFrameTime < this.targetFramerate * 0.92))
                {
                    this.targetFramerate = Math.Max(MinFramerate, (this.targetFramerate + (1000.0 / this.actualFrameTime)) / 2);
                }

                if (now > this.predNextFrame)
                {
                    this.predNextFrame = now;
                }
                else
                {
                    double milliseconds = this.predNextFrame.Subtract(now).TotalMilliseconds;
                    if (milliseconds >= TimerResolution)
                    {
                        Thread.Sleep((int)(milliseconds + 0.5));
                    }
                }

                this.predNextFrame += TimeSpan.FromMilliseconds(1000.0 / this.targetFramerate);

                this.Dispatcher.Invoke(DispatcherPriority.Send, new Action<int>(this.HandleGameTimer), 0);
            }
        }

        private void HandleGameTimer(int param)
        {
            // Every so often, notify what our actual framerate is
            if ((this.frameCount % 100) == 0)
            {
                this.myFallingThings.SetFramerate(1000.0 / this.actualFrameTime);
            }

            // Advance animations, and do hit testing.
            for (int i = 0; i < NumIntraFrames; ++i)
            {
                foreach (var pair in this.players)
                {
                    HitType hit = this.myFallingThings.LookForHits(pair.Value.Segments, pair.Value.GetId());
                    if ((hit & HitType.Squeezed) != 0)
                    {
                        this.squeezeSound.Play();
                    }
                    else if ((hit & HitType.Popped) != 0)
                    {
                        this.popSound.Play();
                    }
                    else if ((hit & HitType.Hand) != 0)
                    {
                        this.hitSound.Play();
                    }
                }

                this.myFallingThings.AdvanceFrame();
            }

            // Draw new Wpf scene by adding all objects to canvas
            playfield.Children.Clear();
            this.myFallingThings.DrawFrame(this.playfield.Children);
            foreach (var player in this.players)
            {
                player.Value.Draw(playfield.Children);
            }

            BannerText.Draw(playfield.Children);
            FlyingText.Draw(playfield.Children);

            this.CheckPlayers();
        }
        #endregion GameTimer/Thread
   
    }
}
