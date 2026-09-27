function P = mckibben_force_to_pressure(F, flexDeg, p)
%MCKIBBEN_FORCE_TO_PRESSURE  Luc keo mong muon (N) -> ap suat can bom (Pa).
%
%   P = mckibben_force_to_pressure(F, flexDeg, p)
%
%   Dung mo hinh tinh cua co McKibben (Chou & Hannaford, 1996):
%       F = P * (pi*D0^2/4) * (3*(1-eps)^2*cos(theta0)^2 - 1) / sin(theta0)^2
%   dao nguoc de tinh P tu F. eps (muc co cua ong, 0 = chua co) uoc luong tu
%   goc gap ngon (ong chay doc ngon: ngon gap -> ong bi keo dai / co lai).
%
%   F       : luc mong muon (N), vo huong hoac vector
%   flexDeg : tong goc gap ngon (do), Unity gui kem trong goi tin GF
%   p       : struct thong so -- xem glove_force_receiver.m (PHAI DO THAT tren gang)
%       p.D0          duong kinh ong luc chua bom (m)
%       p.theta0      goc soi dan so voi truc ong luc chua bom (rad)
%       p.epsAtRest   muc co cua ong khi ngon DUOI THANG (0..~0.25)
%       p.epsPerDeg   muc co thay doi moi do gap ngon (dau: am neu gap ngon keo dai ong)
%       p.Pmax        ap suat toi da an toan (Pa)
%
%   LUU Y: day chi la DIEM BAT DAU. Co that lech khoi cong thuc (do day thanh
%   ong, ma sat, tre). Nen do vai cap (ap suat, luc) bang load cell o vai muc
%   co, roi khop lai he so (vd thay p.D0 / p.theta0 bang gia tri khop duoc).

    eps = p.epsAtRest + p.epsPerDeg .* flexDeg;
    eps = min(max(eps, 0), 0.3);

    A = pi * p.D0^2 / 4;
    k = (3 .* (1 - eps).^2 .* cos(p.theta0)^2 - 1) ./ sin(p.theta0)^2;

    P = zeros(size(F));
    ok = k > 0.05;                      % k ~ 0: ong da co gan het, khong tao them luc duoc
    P(ok) = F(ok) ./ (A .* k(ok));
    P = min(max(P, 0), p.Pmax);         % an toan: khong am, khong vuot Pmax
end
