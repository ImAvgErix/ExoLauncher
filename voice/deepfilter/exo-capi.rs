// ---- Exo Launcher additions to DeepFilterNet 0.5.6's C interface (MIT/Apache-2.0, as the rest of libDF), appended to libDF/src/capi.rs by voicebuild.ps1 ----

/// Exo: a mono DeepFilterNet with the model built into this library (no file), at this attenuation limit.
/// Null when it can't be made.
#[no_mangle]
pub unsafe extern "C" fn df_create_default(atten_lim: f32) -> *mut DFState {
    match std::panic::catch_unwind(|| {
        let r_params = RuntimeParams::default_with_ch(1).with_atten_lim(atten_lim);
        DfTract::new(DfParams::default(), &r_params).ok()
    }) {
        Ok(Some(m)) => Box::into_raw(Box::new(DFState(m))),
        _ => std::ptr::null_mut(),
    }
}

/// Exo: the thresholds (dB of local SNR) for skipping stages: under min, a zero mask; over max_erb, nothing; over
/// max_df, the first stage alone.
#[no_mangle]
pub unsafe extern "C" fn df_set_thresholds(st: *mut DFState, min_db: f32, max_erb_db: f32, max_df_db: f32) {
    if let Some(state) = st.as_mut() {
        state.0.min_db_thresh = min_db;
        state.0.max_db_erb_thresh = max_erb_db;
        state.0.max_db_df_thresh = max_df_db;
    }
}

/// Exo: df_process_frame that never unwinds into the caller: NaN when the frame couldn't be processed.
#[no_mangle]
pub unsafe extern "C" fn df_process_frame_checked(
    st: *mut DFState,
    input: *mut c_float,
    output: *mut c_float,
) -> c_float {
    let Some(state) = st.as_mut() else { return f32::NAN };
    let hop = state.0.hop_size;
    let state = std::panic::AssertUnwindSafe(state);
    match std::panic::catch_unwind(move || {
        let state = state;
        let input = ArrayView2::from_shape_ptr((1, hop), input);
        let output = ArrayViewMut2::from_shape_ptr((1, hop), output);
        state.0 .0.process(input, output).unwrap_or(f32::NAN)
    }) {
        Ok(lsnr) => lsnr,
        Err(_) => f32::NAN,
    }
}
