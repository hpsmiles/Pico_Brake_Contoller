using System.Windows.Forms;

namespace BrakeCalibrator;

public class MainForm : Form
{
    public MainForm()
    {
        Text = "Brake Controller Calibrator";
        Size = new System.Drawing.Size(900, 600);
        MinimumSize = new System.Drawing.Size(850, 550);
    }
}
