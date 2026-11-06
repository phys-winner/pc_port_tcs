using UnityEngine;
using UnityEngine.UI;

namespace ClosingShift.Android
{
    public sealed class AndroidDashedOutline : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect box = rectTransform.rect;
            float radius = Mathf.Min(18, Mathf.Min(box.width, box.height) * .25f);
            float horizontal = box.width - 2 * radius - 6, vertical = box.height - 2 * radius - 6;
            float arc = Mathf.PI * radius * .5f, perimeter = 2 * (horizontal + vertical) + 4 * arc;
            for (float along = 0; along < perimeter; along += 16)
            {
                Vector2 a = Point(along, box, radius, horizontal, vertical, arc);
                Vector2 b = Point(Mathf.Min(along + 9, perimeter), box, radius, horizontal, vertical, arc);
                Vector2 normal = new Vector2(-(b - a).y, (b - a).x).normalized * 1.1f;
                int start = mesh.currentVertCount;
                mesh.AddVert(a - normal, color, Vector2.zero); mesh.AddVert(a + normal, color, Vector2.zero);
                mesh.AddVert(b + normal, color, Vector2.zero); mesh.AddVert(b - normal, color, Vector2.zero);
                mesh.AddTriangle(start, start + 1, start + 2); mesh.AddTriangle(start, start + 2, start + 3);
            }
        }
        static Vector2 Point(float distance, Rect box, float radius, float horizontal, float vertical, float arc)
        {
            for (int side = 0; side < 4; ++side)
            {
                float length = side % 2 == 0 ? horizontal : vertical;
                Vector2 center = new Vector2(side == 0 || side == 3 ? box.xMin + radius + 3 : box.xMax - radius - 3,
                    side < 2 ? box.yMax - radius - 3 : box.yMin + radius + 3);
                float angle = (180 - side * 90) * Mathf.Deg2Rad;
                if (distance <= arc) return center + new Vector2(Mathf.Cos(angle - distance / radius), Mathf.Sin(angle - distance / radius)) * radius;
                distance -= arc;
                Vector2 start = center + new Vector2(Mathf.Cos(angle - Mathf.PI * .5f), Mathf.Sin(angle - Mathf.PI * .5f)) * radius;
                Vector2 direction = side == 0 ? Vector2.right : side == 1 ? Vector2.down : side == 2 ? Vector2.left : Vector2.up;
                if (distance <= length) return start + direction * distance;
                distance -= length;
            }
            return new Vector2(box.xMin + 3, box.yMax - radius - 3);
        }
    }
}
