# Final empirical force equation and coefficient specification

## Variables

- \(P\): pressure in **bar**, with the same pressure reference as the supplied recordings.
- \(L_s\): nominal tether-setting label in **mm**, restricted to \([55,80]\).
- \(b\): `pressurization` or `release`.
- \(\widehat F\): fitted load-cell reading in **N**, including the recorded offsets.
- \(\ell_j\in\{55,60,65,70,75,80\}\): six fitted setting nodes.

The two-stage equation below defines the entire inference function. All learned numbers are in `actuator_coefficients.json`; no optimization takes place when predicting.

## 1. Pressure polynomial at each setting node

For every setting \(j\) and branch \(b\), let the exported ordered pressure breakpoints be \(p_{jb,0},\ldots,p_{jb,n}\). First use

\[
P_{jb}^{*}=\min\{\max(P,p_{jb,0}),p_{jb,n}\}.
\]

Find interval \(i\) with \(p_{jb,i}\le P_{jb}^{*}\le p_{jb,i+1}\); use the right interval at an interior breakpoint and the last interval at the upper endpoint. With \(\Delta P=P_{jb}^{*}-p_{jb,i}\), evaluate

\[
G_j(P,b)=a_{3,jbi}\Delta P^3+a_{2,jbi}\Delta P^2+a_{1,jbi}\Delta P+a_{0,jbi}.
\]

The array `coefficients_a3_a2_a1_a0[i]` is **[a3, a2, a1, a0]**, in descending power order, applied to the **local coordinate \(\Delta P\)**. The units are N/bar³, N/bar², N/bar and N. Do not substitute the absolute pressure for \(\Delta P\), reverse the coefficient order or round the exported values.

## 2. Length interpolation

At the requested pressure and branch, compute all six values \(G_j\). Define

\[
h_j=\ell_{j+1}-\ell_j,
\qquad \delta_j=\frac{G_{j+1}-G_j}{h_j}.
\]

For each interior node, \(D_j=0\) if either neighboring slope is zero or their signs differ. Otherwise,

\[
D_j=\frac{w_1+w_2}{w_1/\delta_{j-1}+w_2/\delta_j},
\qquad w_1=2h_j+h_{j-1},\quad w_2=h_j+2h_{j-1}.
\]

For the first endpoint, form

\[
D_0^{*}=\frac{(2h_0+h_1)\delta_0-h_0\delta_1}{h_0+h_1}.
\]

Set \(D_0=0\) if \(D_0^{*}\) has a different sign from \(\delta_0\), including a zero \(\delta_0\). Otherwise, if the signs of \(\delta_0,\delta_1\) differ and \(|D_0^{*}|>3|\delta_0|\), set \(D_0=3\delta_0\); in all other cases use \(D_0=D_0^{*}\). Apply the same rule from the opposite end, with the last interval and its neighboring interval, to obtain the final endpoint slope. Zero slopes follow the conventions implemented in `_edge_slope`.

For \(L_s\in[\ell_j,\ell_{j+1}]\), let \(u=(L_s-\ell_j)/h_j\). The final equation is

\[
\boxed{\widehat F(P,L_s,b)=
(2u^3-3u^2+1)G_j
+(u^3-2u^2+u)h_jD_j
+(-2u^3+3u^2)G_{j+1}
+(u^3-u^2)h_jD_{j+1}.}
\]

At an exact fitted setting \(L_s=\ell_j\), return \(G_j\). The length slopes \(D_j\) are calculated from the six pressure-polynomial values at the current query; they are not additional fitted constants. This detail is necessary to reproduce sequential PCHIP interpolation exactly.

PCHIP's local slope construction follows the [official SciPy PchipInterpolator specification](https://docs.scipy.org/doc/scipy/reference/generated/scipy.interpolate.PchipInterpolator.html). Pressure-polynomial coefficients use SciPy's local power basis. The supplied Python runtime implements the length interpolation directly without importing SciPy.

## Coefficient identification

At each setting and branch, the 7,000 conditioned samples per recording were separated by the known pump phase, assigned to 64 uniformly spaced pressure bins, and summarized by bin means. Bins with fewer than five samples were omitted. Weighted decreasing isotonic regression was applied to force means, using sample count as weight; PCHIP was then constructed from the resulting pressure/force nodes. All 14 cycles of all six recordings were used in the final refit. Hyperparameters were retained from the previous training-only selection rather than optimized against the final 70-mm fit error.

The number of supported breakpoints can differ between curves. Read the exported interval tables; do not assume 64 polynomial intervals. The JSON also includes the force values at the pressure knots for inspection, although the runtime evaluates the exported polynomial coefficients directly.

This is a branch-dependent empirical force calibration. It contains neither identified hyperelastic material constants nor a derivation of a mechanics law. It should be reported in the paper with that scope.
