using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.UI
{
    public class PicoBridgePanelView : MonoBehaviour
    {
        [Header("Root")]
        public CanvasGroup rootCanvasGroup;
        public Image panelImage;
        public RectTransform panelContentRoot;

        [Header("Connection")]
        public Image statusPillImage;
        public TMP_Text statusPillText;
        public TMP_Text endpointText;

        [Header("Tracking")]
        public Image[] trackingSignalImages;
        public TMP_Text[] trackingSignalLabels;

        [Header("Camera")]
        public RectTransform cameraPreviewRoot;
        public RawImage cameraPreviewImage;
        public TMP_Text cameraStatusText;

        [Header("Controls")]
        public Slider uiOpacitySlider;
        public Button collapseButton;
        public Image collapseButtonIcon;
        public Button immersiveButton;

        [Header("Server URL")]
        public TMP_InputField urlOctetAInput;
        public TMP_InputField urlOctetBInput;
        public Button applyUrlButton;

        [Header("Audio")]
        public Button audioButton;
        public Button microphoneMuteButton;
        public TMP_Text audioStatusText;

        [Header("Stream Resolution")]
        public Button resolution720Button;
        public Button resolution1080Button;
    }
}
