using System;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.ObjectUI.InGame.MessagePanel;
using ExposureUnnoticed2.ObjectUI.InGame.RingMenu;
using ExposureUnnoticed2.Object3D.InteractArea;
using ExposureUnnoticed2.Scripts.InGame;
using ExposureUnnoticed2.Scripts.Sound;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SfmKit.Game
{
    /// <summary>Small questions about the game's UI and feedback to the player. Each call fails safe (false / nothing).</summary>
    internal sealed class GameUi
    {
        private Transform _uiLayer;
        private Il2CppArrayBase<InteractAreaController> _areas;
        private float _areasTime = -10f;

        /// <summary>A game panel (menu, shop, closet ...) is open: children of <c>InGameCanvas/MiddleCloneLayer</c> are
        /// active [observed 2026-10-11, SpreaderBar].</summary>
        public bool PanelOpen()
        {
            if (_uiLayer == null)
            {
                var go = GameObject.Find("/InGameManager/InGameCanvas/MiddleCloneLayer");
                if (go == null) return false;
                _uiLayer = go.transform;
            }
            for (int i = 0; i < _uiLayer.childCount; i++)
                if (_uiLayer.GetChild(i).gameObject.activeSelf) return true;
            return false;
        }

        public static bool RingOpen()
        {
            var view = RingMenuParentView.Instance;
            return view != null && view.IsOpenRing;
        }

        /// <summary>The player stands in an interaction area (closet, PC, machines ...). <c>InteractManager.CurrentInteractModel</c>
        /// stayed null although the closet prompt was shown, so the areas' own flag is used [observed 2026-10-11, SpreaderBar].</summary>
        public bool InInteractArea()
        {
            if (_areas == null || Time.time - _areasTime > 2f) // areas come and go with stages and NPCs
            {
                _areas = UnityEngine.Object.FindObjectsOfType<InteractAreaController>();
                _areasTime = Time.time;
            }
            foreach (var area in _areas)
                if (area != null && area.isInArea && area.gameObject.activeInHierarchy) return true;
            return false;
        }

        /// <summary>One of the player's sound effects (equip, unlock ...).</summary>
        public static void PlaySe(InGameSoundManager.SeType type)
        {
            try
            {
                var facade = PlayerFacade.Instance;
                if (facade != null) facade.PlaySe(type);
            }
            catch (Exception) { }
        }

        /// <summary>A line in the game's message panel (top left; Caution = the warning style). [unverified until a mod shows one]</summary>
        public static void Message(string text, MessagePanelView.MessageType type = MessagePanelView.MessageType.Caution)
        {
            try { if (MessagePanelView.Instance != null) MessagePanelView.AddMessage(text, type); }
            catch (Exception) { }
        }
    }
}
