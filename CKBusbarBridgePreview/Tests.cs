using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.ServiceModel.Configuration;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static CKBusbarBridgePreview.Body2AndFile;

namespace CKBusbarBridgePreview
{
    public partial class Tests : Form
    {
        private ISldWorks swApp;
        public Tests(ISldWorks swApp1)
        {
            InitializeComponent();
            this.swApp = swApp1;
        }

        private List<FreeDrawBody> fdBodys = new List<FreeDrawBody>();

        private void button12_Click(object sender, EventArgs e)
        {
            if (swApp == null)
            {
                return;
            }

            fdBodys.ForEach(x => x.Hide());
            fdBodys.Clear();

            BusDuctData busDuctData = new BusDuctData();

            busDuctData.PhaseNum = 3;
            busDuctData.AllBusbarPhase_e = new List<BusbarPhase_e>()
                { BusbarPhase_e.A, BusbarPhase_e.B, BusbarPhase_e.C };
            busDuctData.BusbarNum = 4;
            busDuctData.PhaseDis = 210.0;
            busDuctData.OtherPositions = new List<double>() { 2000, 3500 };
            //busDuctData.StartPositionLeft = (800.0 - busDuctData.PhaseDis * (busDuctData.PhaseNum - 1)) / 2.0;
            busDuctData.StartSegmentation = -200.0;
            busDuctData.EndSegmentation = -200.0;

            busDuctData.BendSetting = new BendPreviewSetting();
            busDuctData.BendSetting.BusBarThickness = 8.0;
            busDuctData.BendSetting.BarBendingRadius = 10.0;
            busDuctData.BendSetting.BusBarWidth = 80.0;

            busDuctData.XVector = new Vector3d(1, 0, 0);
            busDuctData.YVector = new Vector3d(0, 0, 1);
            var ZVector = busDuctData.XVector.Cross(busDuctData.YVector).Negate().Normalized;
            busDuctData.BendSetting.StartBusbarVector = ZVector;
            busDuctData.BendSetting.WidthVector = ZVector.Cross(busDuctData.XVector).Negate();

            busDuctData.BusbarStartPoint_B = new Point3d(579.5, 2620 - 80, 500);
            busDuctData.A_BDis = 5928;

            var baseFs = new List<BaseFrameData>();
            var startP = new Point3d(800, 2820 + 251, 500);
            
            var oldBaseFrame = new BaseFrameData();
            for (int i = 0; i < 6; i++)
            {
                var gdBaseFrame = oldBaseFrame;

                gdBaseFrame.Height = 502;
                gdBaseFrame.Width = 800;
                gdBaseFrame.Length = i == 2 ? 460 : 1000; ;

                gdBaseFrame.XVector3d = busDuctData.XVector;
                gdBaseFrame.YVector3d = busDuctData.YVector;
                if (i != 0)
                {
                    gdBaseFrame.CenterPoint = gdBaseFrame.CenterPoint.Move(gdBaseFrame.XVector3d,
                        (baseFs[i - 1].Length + gdBaseFrame.Length) / 2.0);
                }
                else
                {
                    gdBaseFrame.CenterPoint = startP.Move(gdBaseFrame.XVector3d,gdBaseFrame.Length / 2.0);
                }

                if (i > 2)
                {
                    gdBaseFrame.bReverse = true;
                }
                else
                {
                    gdBaseFrame.bReverse = false;
                }

                if (gdBaseFrame.bReverse)
                {
                    gdBaseFrame.XVector3d = gdBaseFrame.XVector3d.Negate().Normalized;
                    gdBaseFrame.YVector3d = gdBaseFrame.YVector3d.Negate().Normalized;
                }

                gdBaseFrame.InsulatorNum = 1;
            
                gdBaseFrame.InsulatorDic = new List<double>() { gdBaseFrame.Length / 3.0 };
                gdBaseFrame.InsulatorLeftDic = (gdBaseFrame.Width - busDuctData.PhaseDis * (busDuctData.PhaseNum - 1)) / 2.0;

                baseFs.Add(gdBaseFrame);

                oldBaseFrame = gdBaseFrame.Copy();
            }

            busDuctData.BasesFrames = baseFs;

            BusDuctPreview busDuct = new BusDuctPreview(swApp);
            fdBodys = busDuct.BaseFramePreview(busDuctData);

            var fdBodys2 = busDuct.InsulatorPreview(busDuctData);
            fdBodys.AddRange(fdBodys2);

            var fdBodys3 = busDuct.BusbarPreview(busDuctData);
            fdBodys.AddRange(fdBodys3);

            fdBodys.ForEach(x => x.Show());
        }

        private void button1_Click(object sender, EventArgs e)
        {

        }

        private void Tests_Load(object sender, EventArgs e)
        {
        
        }

        private void button2_Click(object sender, EventArgs e)
        {
            fdBodys.ForEach(x => x.Hide());
            fdBodys.Clear();
        }

        private void button3_Click(object sender, EventArgs e)
        {
            var swModel = swApp.IActiveDoc2;
            var swPart = swModel as PartDoc;

            var body = ((object[])swPart.GetBodies(0)).Cast<Body2>().First();

            BodyHelper.SaveBodyToFile(body,$@"F:\绝缘子.dat");
        }

        private void button4_Click(object sender, EventArgs e)
        {
            var swModel = swApp.IActiveDoc2;
            var swAddDoc = swModel as AssemblyDoc;
            var compB = swAddDoc.GetComponentByName("B-1-1-1");
            //PublicMethod.AddMateUseCoord(swApp, null, compB, "B柜-出");

            for (int i = 1; i < 6; i++)
            {
                var compA = swAddDoc.GetComponentByName($"B-1-{i}-1");
                compB = swAddDoc.GetComponentByName($"B-1-{i + 1}-1");

                //PublicMethod.AddMateUseCoord(swApp, compA, compB);
            }

            swModel.EditRebuild3();
        }
    }
}
