using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace COIJointVentures.UI;

internal sealed class ModUIManager
{
    private GameObject? _uiGameObject;
    private UIDocument? _uiDocument;
    private PanelSettings? _panelSettings;

    public VisualElement? RootElement => _uiDocument?.rootVisualElement;
    public bool IsInitialized => _uiDocument != null;

    public void Initialize()
    {
        if (_uiGameObject != null) return;

        _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        _panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
        _panelSettings.sortingOrder = 1000;

        // steal the theme from an existing UIDocument so we get fonts
        var existingTheme = FindExistingTheme();
        if (existingTheme != null)
        {
            _panelSettings.themeStyleSheet = existingTheme;
            Plugin.LogInstance.LogInfo("Grabbed theme from existing UIDocument.");
        }
        else
        {
            Plugin.LogInstance.LogWarning("No existing UIElements theme found — text may not render.");
        }

        _uiGameObject = new GameObject("JointVenturesUI");
        UnityEngine.Object.DontDestroyOnLoad(_uiGameObject);

        _uiDocument = _uiGameObject.AddComponent<UIDocument>();
        _uiDocument.panelSettings = _panelSettings;

        var root = _uiDocument.rootVisualElement;
        root.style.position = Position.Absolute;
        root.style.left = 0;
        root.style.top = 0;
        root.style.right = 0;
        root.style.bottom = 0;
        root.pickingMode = PickingMode.Ignore;

        Plugin.LogInstance.LogInfo("UIElements panel initialized.");
    }

    public void AddElement(VisualElement element)
    {
        Initialize();
        RootElement?.Add(element);
    }

    public void Dispose()
    {
        if (_uiGameObject != null)
        {
            UnityEngine.Object.Destroy(_uiGameObject);
            _uiGameObject = null;
            _uiDocument = null;
        }

        if (_panelSettings != null)
        {
            UnityEngine.Object.Destroy(_panelSettings);
            _panelSettings = null;
        }
    }

    private static ThemeStyleSheet? FindExistingTheme()
    {
        // check existing PanelSettings assets
        var allPanelSettings = Resources.FindObjectsOfTypeAll<PanelSettings>();
        foreach (var ps in allPanelSettings)
        {
            if (ps.themeStyleSheet != null)
                return ps.themeStyleSheet;
        }

        // check existing UIDocuments
        var allDocs = Resources.FindObjectsOfTypeAll<UIDocument>();
        foreach (var doc in allDocs)
        {
            if (doc.panelSettings?.themeStyleSheet != null)
                return doc.panelSettings.themeStyleSheet;
        }

        return null;
    }
}
