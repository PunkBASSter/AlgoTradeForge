export interface ThresholdProfileResponse {
  name: string;
  isBuiltIn: boolean;
  profileJson: string;
}

export interface CreateThresholdProfileRequest {
  name: string;
  profileJson: string;
}
