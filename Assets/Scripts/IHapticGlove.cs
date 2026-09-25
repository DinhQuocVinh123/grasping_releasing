using UnityEngine;

/// <summary>Ngon co bo phat haptic tren gang (gang dang lam chi co ngon cai + tro).</summary>
public enum HapticFinger
{
    Thumb = 0,
    Index = 1,
}

/// <summary>Cac su kien tiep xuc. Moi su kien kem 1 xung rung ngan (transient).</summary>
public enum HapticEventType
{
    Touch,    // da ngon vua cham be mat vat
    Untouch,  // da ngon vua roi khoi be mat
    Grab,     // vat vua dinh vao tay (bop tu 2 phia)
    Release,  // vat vua duoc tha ra
}

/// <summary>Mot xung rung ngan: dao dong tat dan, kieu
/// bien_do * e^(-t/thoi_gian) * sin(2*pi*tan_so*t). Driver cua tung loai gang
/// tu quyet dinh tai tao the nao cho hop voi phan cung cua no.</summary>
[System.Serializable]
public struct HapticTransient
{
    [Range(0f, 1f)] public float amplitude;
    [Tooltip("Tan so (Hz). Thap = cham em/mem, cao = cham gat/cung.")]
    public float frequencyHz;
    [Tooltip("Thoi gian (giay) de xung tat dan.")]
    public float durationSeconds;

    public HapticTransient(float amplitude, float frequencyHz, float durationSeconds)
    {
        this.amplitude = amplitude;
        this.frequencyHz = frequencyHz;
        this.durationSeconds = durationSeconds;
    }

    public HapticTransient Scaled(float factor) =>
        new HapticTransient(Mathf.Clamp01(amplitude * factor), frequencyHz, durationSeconds);
}

/// <summary>
/// "O cam" chung cho MOI thiet bi haptic: gang that (sau nay), rung tay cam
/// Quest, bang debug... HapticRenderer chi noi chuyen voi interface nay, nen
/// khi gang san sang chi can viet 1 class moi implement no -- khong phai dong
/// vao phan tinh toan.
///
/// Hai loai tin hieu, theo Kuchenbecker et al. (IEEE TVCG 2006): luc LIEN TUC
/// theo do lun, cong XUNG NGAN luc bat dau cham -- chi co luc lien tuc thi vat
/// ao cam giac nhu xop, them xung thi that hon han.
/// </summary>
public interface IHapticGlove
{
    /// <summary>Goi MOI KHUNG HINH. 0 = khong cham, 1 = luc toi da.</summary>
    void SetPressure(HapticFinger finger, float pressure01);

    /// <summary>Goi 1 lan khi co su kien tiep xuc.</summary>
    void PlayTransient(HapticFinger finger, HapticEventType type, HapticTransient transient);
}
