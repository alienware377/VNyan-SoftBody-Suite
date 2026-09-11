using UnityEngine;
using UnityEngine.EventSystems;

namespace SoftBodyStudio
{
    // Makes a UI panel draggable. Attach to the panel root; dragging anywhere on the
    // attached object moves the panel within its canvas. Child controls (sliders,
    // dropdowns, buttons) consume their own pointer events first.
    public class SquishWindowDrag : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        RectTransform rt;
        RectTransform canvasRt;
        Vector2 grabOffset;

        void Awake()
        {
            rt = GetComponent<RectTransform>();
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas != null) canvasRt = canvas.transform as RectTransform;
        }

        public void OnPointerDown(PointerEventData e)
        {
            rt.SetAsLastSibling();
            if (canvasRt == null) return;
            Vector2 lp;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRt, e.position, e.pressEventCamera, out lp))
                grabOffset = (Vector2)rt.localPosition - lp;
        }

        public void OnDrag(PointerEventData e)
        {
            if (canvasRt == null) return;
            Vector2 lp;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRt, e.position, e.pressEventCamera, out lp))
                rt.localPosition = lp + grabOffset;
        }
    }
}
