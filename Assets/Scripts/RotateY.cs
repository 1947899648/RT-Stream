using UnityEngine;

public class RotateY : MonoBehaviour
{
    public float speed = 90f;

    void Update()
    {
        transform.Rotate(Vector3.up, speed * Time.deltaTime);
    }
}
