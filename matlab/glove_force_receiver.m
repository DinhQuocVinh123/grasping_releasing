%GLOVE_FORCE_RECEIVER  Nhan LUC MUC TIEU tu Unity (GloveForceOutput) va ve theo thoi gian thuc.
%
%   Unity gui moi goi tin UDP (cong 5010) la 1 dong chu:
%     GF,seq,t,thumbFx,thumbFy,thumbFz,indexFx,indexFy,indexFz,thumbN,indexN,thumbFlexDeg,indexFlexDeg,thumbContact,indexContact
%   - F (N): luc vat ao day len dau ngon, toa do BAN TAY (truc cua xuong co tay trong Unity)
%   - N (N): do lon luc
%   - FlexDeg: tong goc gap ngon (do), de uoc luong muc co cua ong McKibben
%   - Contact: 1 neu dang cham vat
%   Goi tin den DEU (~60/giay) ca khi luc = 0 -> dung lam nhip tim.
%
%   Chay: mo MATLAB, cd toi thu muc nay, go  glove_force_receiver
%   Dung: dong cua so do thi.
%
%   AN TOAN: neu khong nhan duoc goi tin nao trong WATCHDOG_S giay, dat ap suat
%   muc tieu = 0 (xa ap). Nen lam THEM mot co che tuong tu ngay tren Arduino,
%   phong khi chinh MATLAB bi treo.

PORT = 5010;
WATCHDOG_S = 0.2;
WINDOW_S = 10;          % do rong cua so do thi (giay)

% --- Thong so actuator: PHAI DO THAT tren gang (dang la gia tri minh hoa) ---
act.D0 = 0.006;                 % m
act.theta0 = deg2rad(25);       % rad
act.epsAtRest = 0.0;
act.epsPerDeg = 0.0;            % chua biet -> tam bo qua anh huong goc gap ngon
act.Pmax = 150e3;               % Pa -- dat theo gioi han an toan cua he thong

% --- Arduino (tuy chon): bo ghi chu va sua cho khop firmware cua nhom ---
arduino = [];
% arduino = serialport("COM5", 115200);   % doi COM cho dung

u = udpport("datagram", "IPV4", "LocalPort", PORT);
cleanup = onCleanup(@() delete(u));
fprintf("Dang nghe UDP cong %d ...\n", PORT);

fig = figure("Name", "Luc muc tieu tu Unity", "NumberTitle", "off");
tiledlayout(fig, 2, 1);
ax1 = nexttile; hold(ax1, "on"); grid(ax1, "on");
lt = animatedline(ax1, "Color", [0.9 0.5 0], "LineWidth", 1.5, "DisplayName", "ngon cai");
li = animatedline(ax1, "Color", [0 0.6 0.9], "LineWidth", 1.5, "DisplayName", "ngon tro");
ylabel(ax1, "Luc (N)"); legend(ax1, "Location", "northwest");
ax2 = nexttile; hold(ax2, "on"); grid(ax2, "on");
pt = animatedline(ax2, "Color", [0.9 0.5 0], "LineWidth", 1.5);
pi_ = animatedline(ax2, "Color", [0 0.6 0.9], "LineWidth", 1.5);
ylabel(ax2, "Ap suat muc tieu (kPa)"); xlabel(ax2, "thoi gian (s)");

t0 = tic; lastRx = tic; lastSeq = -1; lost = 0;
Ptarget = [0 0];
while isvalid(fig)
    if u.NumDatagramsAvailable > 0
        dg = read(u, u.NumDatagramsAvailable, "string");
        msg = dg(end).Data;                        % chi can goi MOI NHAT
        v = str2double(split(string(msg), ","));
        if numel(v) == 15 && startsWith(string(msg), "GF")
            seq = v(2);
            if lastSeq >= 0 && seq > lastSeq + 1, lost = lost + (seq - lastSeq - 1); end
            lastSeq = seq; lastRx = tic;
            Fn = [v(10) v(11)];                    % [ngon cai, ngon tro] (N)
            flex = [v(12) v(13)];
            Ptarget = mckibben_force_to_pressure(Fn, flex, act);
            t = toc(t0);
            addpoints(lt, t, Fn(1)); addpoints(li, t, Fn(2));
            addpoints(pt, t, Ptarget(1) / 1e3); addpoints(pi_, t, Ptarget(2) / 1e3);
            xlim(ax1, [max(0, t - WINDOW_S) max(WINDOW_S, t)]); xlim(ax2, xlim(ax1));
            title(ax1, sprintf("seq %d | mat %d goi | cham: cai %d, tro %d", seq, lost, v(14), v(15)));
        end
    end
    if toc(lastRx) > WATCHDOG_S
        Ptarget = [0 0];                           % mat tin hieu -> xa ap
    end
    if ~isempty(arduino)
        writeline(arduino, sprintf("P,%.0f,%.0f", Ptarget(1), Ptarget(2)));   % sua cho khop firmware
    end
    drawnow limitrate;
    pause(0.005);
end
